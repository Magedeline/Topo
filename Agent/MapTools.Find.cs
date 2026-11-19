using System.Globalization;
using System.Text.Json.Nodes;

namespace CELMapViewer.Agent;

/// <summary>Find in map (flags, dialog keys, any attribute) and camera replay of in-game recordings.</summary>
public sealed partial class MapTools
{
    /// <summary>Test hook: read camera recordings from here instead of Celeste/ModFiles/CameraSim/Runs.</summary>
    internal static string? CameraRunsDirOverride;

    private void RegisterFind()
    {
        Add("find_in_map", "Find placed entities, triggers, decals and rooms by name, attribute value, session flag or dialog key, the same search as " +
            "the editor's Edit > Find (Alt+F). Use it to answer 'where is flag X set / read', 'which entity uses dialog Y', 'where are all the " +
            "refills'. An empty query with field=flags or field=dialog lists every one. Results carry room, kind, id, type, attribute, value and " +
            "world/room-local position.", true,
            Props(("query", P("string", "Text to find (substring; see whole_word). Empty lists everything of the field.")),
                  ("kind", Enum("Which items (default all).", "all", "entities", "triggers", "decals", "rooms")),
                  ("field", Enum("What to match (default anything): names = type/texture/room names; values = attribute values; " +
                                 "flags = flag attributes, split into single flags; dialog = dialog-key attributes.", "anything", "names", "values", "flags", "dialog")),
                  ("whole_word", P("boolean", "Match whole words only (default false).")),
                  ("case_sensitive", P("boolean", "Default false.")),
                  ("room", RoomP),
                  ("limit", P("integer", "Max results (default 200)."))), None, FindInMap);

        Add("list_flags", "Every session flag used in the map (or one room): which entities/triggers set it and which only read it (a best guess from " +
            "type and attribute names), with positions. Use it to check flag logic: a flag that is read but never set, or set but never read, is usually a bug.", true,
            Props(("room", RoomP)), None, ListFlags);

        Add("camera_recordings", "Camera recordings made in game with the Lönn Camera Simulator mod (F8 in a level; files in Celeste/ModFiles/CameraSim/Runs), " +
            "newest first, with map, start room, rooms visited and frame count. Topo only reads them; if there are none, tell the user to install that " +
            "mod from https://gamebanana.com/tools/25020 and record.", true, new(), None, CameraRecordings);

        Add("camera_replay", "Re-play a camera recording against the map AS IT IS NOW and compare Topo's simulated camera (Celeste's camera rules: room bounds " +
            "and camera offset, cameraOffset/cameraTarget/cameraAdvanceTarget/everest smoothCameraOffset triggers, killbox lower bound, catch-up easing) with " +
            "the camera the game recorded. Edit camera triggers, then run this again to see the effect without going back into the game. Returns per-room " +
            "summaries, the largest differences, triggers that were active, unsupported camera triggers, and sampled frames. Player physics are not " +
            "re-simulated: the recorded path is used.", true,
            Props(("recording", P("string", "File name from camera_recordings (default: the latest).")),
                  ("room", P("string", "Only report frames in this room.")),
                  ("flags", P("object", "Force flags for the simulation, e.g. {\"door_open\": true, \"boss\": false}.")),
                  ("samples", P("integer", "How many evenly spaced frames to list (default 12, max 200)."))), None, CameraReplay);
    }

    private static JsonObject HitJson(SearchHit h)
    {
        var o = new JsonObject { ["room"] = h.Room.Name, ["kind"] = h.KindLabel.ToLowerInvariant() };
        if (h.El != null)
        {
            if (h.El.Find("id") != null) o["id"] = h.El.Int("id");
            o["type"] = h.Name;
            o["x"] = h.El.Num("x"); o["y"] = h.El.Num("y");
        }
        if (h.Field is not ("name" or "texture")) { o["attribute"] = h.Field; o["value"] = h.Value; }
        o["world"] = new JsonArray(h.World.X, h.World.Y);
        return o;
    }

    private ToolResult FindInMap(JsonObject a) => _host.Invoke(() =>
    {
        var map = MapOrThrow();
        var kind = (S(a, "kind") ?? "all") switch
        {
            "entities" => SearchKind.Entities, "triggers" => SearchKind.Triggers, "decals" => SearchKind.Decals, "rooms" => SearchKind.Rooms, _ => SearchKind.All
        };
        var field = (S(a, "field") ?? "anything") switch
        {
            "names" => SearchField.Names, "values" => SearchField.Values, "flags" => SearchField.Flags, "dialog" => SearchField.Dialog, _ => SearchField.Anything
        };
        string query = S(a, "query") ?? "";
        if (query.Trim().Length == 0 && field is SearchField.Anything or SearchField.Names or SearchField.Values && kind == SearchKind.All)
            throw new ToolException("Give a query, or field=flags / field=dialog (or a kind) to list everything of that sort.");
        string? room = S(a, "room");
        if (room is { Length: > 0 }) FindRoom(room);
        int limit = Math.Clamp(I(a, "limit") ?? 200, 1, 5000);
        var hits = MapSearch.Find(map, query, new SearchOptions(kind, field, B(a, "whole_word"), B(a, "case_sensitive"), room)).ToList();
        var arr = new JsonArray();
        foreach (var h in hits.Take(limit)) arr.Add(HitJson(h));
        return ToolResult.Json(new JsonObject
        {
            ["total"] = hits.Count, ["shown"] = arr.Count, ["rooms"] = hits.Select(h => h.Room.Name).Distinct().Count(), ["results"] = arr
        });
    });

    private ToolResult ListFlags(JsonObject a) => _host.Invoke(() =>
    {
        var map = MapOrThrow();
        string? room = S(a, "room");
        if (room is { Length: > 0 }) FindRoom(room);
        var uses = MapSearch.Flags(map, room);
        var arr = new JsonArray();
        foreach (var g in uses.GroupBy(u => u.Flag).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var setters = new JsonArray(); var readers = new JsonArray();
            foreach (var u in g)
            {
                var o = HitJson(u.Hit);
                if (u.Inverted) o["inverted"] = true;
                (u.Sets ? setters : readers).Add(o);
            }
            var f = new JsonObject { ["flag"] = g.Key, ["set_by"] = setters, ["read_by"] = readers };
            if (setters.Count == 0) f["note"] = "read but never set in this map (set by code, another map, or a typo?)";
            else if (readers.Count == 0) f["note"] = "set but never read in this map";
            arr.Add(f);
        }
        return ToolResult.Json(new JsonObject { ["flags"] = arr.Count, ["list"] = arr });
    });

    private static string? RunsDir() => CameraRunsDirOverride ?? CameraRun.RunsDir();

    private ToolResult CameraRecordings(JsonObject a)
    {
        string? dir = RunsDir();
        var runs = CameraRun.List(dir);
        if (runs.Count == 0)
            return ToolResult.Json(new JsonObject
            {
                ["recordings"] = 0, ["folder"] = dir, ["recorder_installed"] = CameraRunsDirOverride == null && CameraRun.RecorderInstalled(),
                ["hint"] = "No recordings. Install the Lönn Camera Simulator mod (https://gamebanana.com/tools/25020) into Celeste/Mods, restart, and press F8 in a level."
            });
        string? latest = CameraRun.LatestName(dir);
        var arr = new JsonArray();
        foreach (var r in runs.Take(50))
        {
            CameraRun full;
            try { full = CameraRun.Load(r.Path); } catch (IOException) { continue; }
            arr.Add(new JsonObject
            {
                ["file"] = r.FileName, ["latest"] = r.FileName == latest, ["modified"] = r.Modified.ToString("s", CultureInfo.InvariantCulture),
                ["map"] = full.Map, ["start_room"] = CameraRun.NormalizeRoom(full.StartRoom), ["frames"] = full.Frames.Count,
                ["seconds"] = Math.Round(full.Frames.Sum(f => f.Dt > 0 ? f.Dt : 1 / 60.0), 2),
                ["rooms"] = new JsonArray(full.Frames.Select(f => CameraRun.NormalizeRoom(f.Room)).Distinct().Select(s => (JsonNode)s!).ToArray()),
                ["deaths"] = full.Deaths.Count, ["ended"] = full.Ended
            });
        }
        return ToolResult.Json(new JsonObject { ["recordings"] = runs.Count, ["folder"] = dir, ["list"] = arr });
    }

    private ToolResult CameraReplay(JsonObject a)
    {
        string? dir = RunsDir() ?? throw new ToolException("Celeste folder not found.");
        string? name = S(a, "recording") is { Length: > 0 } n ? n : CameraRun.LatestName(dir) ?? CameraRun.List(dir).FirstOrDefault()?.FileName;
        if (name == null) throw new ToolException("No camera recordings. Install the Lönn Camera Simulator mod (https://gamebanana.com/tools/25020) and press F8 in a level.");
        string path = Path.Combine(dir, Path.GetFileName(name));
        if (!File.Exists(path)) throw new ToolException($"No recording '{name}' in {dir}. Use camera_recordings.");
        var run = CameraRun.Load(path);
        var overrides = new Dictionary<string, bool>();
        if (a["flags"] is JsonObject fo)
            foreach (var (k, v) in fo) overrides[k] = v is JsonValue jv && jv.TryGetValue(out bool b) ? b : v?.ToString() == "true";
        string? onlyRoom = S(a, "room") is { Length: > 0 } rr ? CameraRun.NormalizeRoom(rr) : null;
        int samples = Math.Clamp(I(a, "samples") ?? 12, 0, 200);

        return _host.Invoke(() =>
        {
            var map = MapOrThrow();
            var res = CameraSim.Run(run, map, overrides);
            var idx = Enumerable.Range(0, run.Frames.Count).Where(i => onlyRoom == null || CameraRun.NormalizeRoom(run.Frames[i].Room) == onlyRoom).ToList();
            var rooms = new JsonArray();
            foreach (var g in idx.GroupBy(i => CameraRun.NormalizeRoom(run.Frames[i].Room)))
            {
                var ds = g.Select(i => res.Divergence[i]).Where(d => !float.IsNaN(d)).ToList();
                int worst = g.OrderByDescending(i => float.IsNaN(res.Divergence[i]) ? -1 : res.Divergence[i]).First();
                rooms.Add(new JsonObject
                {
                    ["room"] = g.Key, ["in_map"] = g.Any(i => res.Frames[i].Room != null),
                    ["frames"] = g.Count(), ["simulated_frames"] = g.Count(i => !res.Frames[i].UsedRecorded),
                    ["max_diff_px"] = Math.Round(ds.DefaultIfEmpty(0).Max(), 1), ["mean_diff_px"] = Math.Round(ds.DefaultIfEmpty(0).Average(), 1),
                    ["worst_frame"] = worst,
                    ["triggers"] = new JsonArray(g.SelectMany(i => res.Frames[i].ActiveTriggers).Distinct()
                        .Select(t => (JsonNode)$"{t.Name}#{t.Int("id")}").ToArray())
                });
            }
            var frames = new JsonArray();
            if (samples > 0 && idx.Count > 0)
                foreach (int i in Enumerable.Range(0, Math.Min(samples, idx.Count)).Select(k => idx[(int)((long)k * (idx.Count - 1) / Math.Max(1, Math.Min(samples, idx.Count) - 1))]).Distinct())
                {
                    var f = run.Frames[i]; var s = res.Frames[i];
                    var o = new JsonObject
                    {
                        ["frame"] = i, ["room"] = CameraRun.NormalizeRoom(f.Room), ["player"] = new JsonArray(f.X, f.Y), ["state"] = f.State,
                        ["simulated_camera"] = new JsonArray(MathF.Round(s.Camera.X, 1), MathF.Round(s.Camera.Y, 1)),
                    };
                    if (f.Camera is { } c) o["recorded_camera"] = new JsonArray(MathF.Round(c.X, 1), MathF.Round(c.Y, 1));
                    if (!float.IsNaN(res.Divergence[i])) o["diff_px"] = MathF.Round(res.Divergence[i], 1);
                    if (s.UsedRecorded) o["uses_recorded"] = true;
                    if (s.ActiveTriggers.Count > 0) o["triggers"] = new JsonArray(s.ActiveTriggers.Select(t => (JsonNode)t.Name).ToArray());
                    frames.Add(o);
                }
            return ToolResult.Json(new JsonObject
            {
                ["recording"] = run.FileName, ["map"] = run.Map, ["frames"] = run.Frames.Count,
                ["max_diff_px"] = Math.Round(idx.Select(i => res.Divergence[i]).Where(d => !float.IsNaN(d)).DefaultIfEmpty(0).Max(), 1),
                ["flag_overrides"] = overrides.Count,
                ["unsupported_camera_triggers"] = new JsonArray(res.Unsupported.Select(u => (JsonNode)u).ToArray()),
                ["rooms"] = rooms, ["samples"] = frames,
                ["note"] = "diff_px is how far the simulated camera is from the one recorded in game. After editing camera triggers a difference is " +
                           "expected: it shows where the new camera goes. Unchanged triggers with large diffs usually mean an unsupported trigger or mod code."
            });
        });
    }
}
