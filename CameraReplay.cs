using System.Globalization;
using System.Text.Json;

namespace CELMapViewer;

/// <summary>One recorded game frame: player position, hitbox, speed, state and the game's real camera.</summary>
public sealed record CamFrame(int I, string Room, float X, float Y, float Vx, float Vy, RectangleF Hitbox, int State, float Dt, PointF? Camera);

public sealed record FlagEvent(int Frame, string[] Set, string[] Clear);

/// <summary>
/// A camera recording made in game by the "Lönn Camera Simulator" Everest mod (gamebanana.com/tools/25020): JSON lines in
/// <c>Celeste/ModFiles/CameraSim/Runs/run_*.jsonl</c>, with <c>latest.txt</c> naming the newest. Topo only reads these files;
/// install that mod and press F8 in game to record. Only the fields Topo uses are parsed, so newer versions keep working.
/// </summary>
public sealed class CameraRun
{
    public string FileName = "", Path = "";
    public string Map = "", StartRoom = "";
    public DateTime Modified;
    public HashSet<string> InitialFlags = new();
    public List<CamFrame> Frames = new();
    public List<FlagEvent> FlagEvents = new();
    public List<int> Deaths = new();
    public bool Ended;
    public long Size;

    public static string? RunsDir(string? celesteDir = null)
    {
        string? dir = celesteDir ?? GameAssets.CelesteDir;
        return dir == null ? null : System.IO.Path.Combine(dir, "ModFiles", "CameraSim", "Runs");
    }

    /// <summary>Is the in-game recorder mod installed (folder or zip under Mods)?</summary>
    public static bool RecorderInstalled(string? celesteDir = null)
    {
        string? dir = celesteDir ?? GameAssets.CelesteDir;
        if (dir == null || !Directory.Exists(System.IO.Path.Combine(dir, "Mods"))) return false;
        string mods = System.IO.Path.Combine(dir, "Mods");
        return Directory.EnumerateFileSystemEntries(mods, "*camerasim*").Any()
               || Directory.EnumerateFileSystemEntries(mods, "*CameraSim*").Any();
    }

    /// <summary>Recordings, newest first (header only: map, start room, time).</summary>
    public static List<CameraRun> List(string? runsDir = null)
    {
        var list = new List<CameraRun>();
        runsDir ??= RunsDir();
        if (runsDir == null || !Directory.Exists(runsDir)) return list;
        foreach (string f in Directory.EnumerateFiles(runsDir, "run_*.jsonl"))
        {
            var run = new CameraRun { FileName = System.IO.Path.GetFileName(f), Path = f };
            try
            {
                var info = new FileInfo(f);
                run.Modified = info.LastWriteTime;
                run.Size = info.Length;
                using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                if (sr.ReadLine() is { } first) run.Apply(first);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            list.Add(run);
        }
        return list.OrderByDescending(r => r.Modified).ToList();
    }

    /// <summary>The file named in latest.txt, if any.</summary>
    public static string? LatestName(string? runsDir = null)
    {
        runsDir ??= RunsDir();
        if (runsDir == null) return null;
        try
        {
            string p = System.IO.Path.Combine(runsDir, "latest.txt");
            return File.Exists(p) ? File.ReadAllText(p).Trim() is { Length: > 0 } s ? s : null : null;
        }
        catch (IOException) { return null; }
    }

    /// <summary>Reads a whole recording. The game may still be writing it; a half-written last line is skipped.</summary>
    public static CameraRun Load(string path)
    {
        var run = new CameraRun { FileName = System.IO.Path.GetFileName(path), Path = path };
        var info = new FileInfo(path);
        run.Modified = info.LastWriteTime;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        run.Size = fs.Length;
        using var sr = new StreamReader(fs);
        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            try { run.Apply(line); }
            catch (JsonException) { }
        }
        return run;
    }

    public static CameraRun Parse(string jsonl, string name = "run.jsonl")
    {
        var run = new CameraRun { FileName = name };
        foreach (string line in jsonl.Split('\n'))
            try { run.Apply(line.Trim()); } catch (JsonException) { }
        return run;
    }

    private void Apply(string line)
    {
        if (line.Length == 0) return;
        using var doc = JsonDocument.Parse(line);
        var o = doc.RootElement;
        if (o.ValueKind != JsonValueKind.Object) return;
        switch (Str(o, "t"))
        {
            case "header":
                Map = Str(o, "map"); StartRoom = Str(o, "room");
                Frames.Clear(); FlagEvents.Clear(); Deaths.Clear(); InitialFlags.Clear();
                if (o.TryGetProperty("flags", out var fl) && fl.ValueKind == JsonValueKind.Array)
                    foreach (var f in fl.EnumerateArray()) InitialFlags.Add(f.ToString());
                break;
            case "f":
            {
                float x = Num(o, "x"), y = Num(o, "y");
                var hb = o.TryGetProperty("l", out _) && o.TryGetProperty("rt", out _)
                    ? RectangleF.FromLTRB(Num(o, "l"), Num(o, "tp"), Num(o, "rt"), Num(o, "bt"))
                    : new RectangleF(x - 4, y - 11, 8, 11);
                PointF? cam = o.TryGetProperty("cam", out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() >= 2
                    ? new PointF(c[0].GetSingle(), c[1].GetSingle()) : null;
                Frames.Add(new CamFrame((int)Num(o, "i", Frames.Count), Str(o, "r"), x, y, Num(o, "vx"), Num(o, "vy"), hb,
                                        (int)Num(o, "st"), Num(o, "dt", 1f / 60), cam));
                break;
            }
            case "flag":
                FlagEvents.Add(new FlagEvent((int)Num(o, "i"), Arr(o, "set"), Arr(o, "clear")));
                break;
            case "death":
                Deaths.Add((int)Num(o, "i"));
                break;
            case "end":
                Ended = true;
                break;
        }
    }

    private static string Str(JsonElement o, string k) => o.TryGetProperty(k, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString() : "";

    private static float Num(JsonElement o, string k, float def = 0) =>
        o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle()
        : o.TryGetProperty(k, out v) && v.ValueKind == JsonValueKind.String && float.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f
        : def;

    private static string[] Arr(JsonElement o, string k) =>
        o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(e => e.ToString()).ToArray() : Array.Empty<string>();

    public static string NormalizeRoom(string r) => r.StartsWith("lvl_") ? r[4..] : r;

    /// <summary>The session flags at each frame, from the header flags and the recorded flag events.</summary>
    public List<HashSet<string>> FlagsPerFrame()
    {
        var byFrame = FlagEvents.GroupBy(e => e.Frame).ToDictionary(g => g.Key, g => g.ToList());
        var cur = new HashSet<string>(InitialFlags);
        var list = new List<HashSet<string>>(Frames.Count);
        foreach (var f in Frames)
        {
            if (byFrame.TryGetValue(f.I, out var evs))
            {
                cur = new HashSet<string>(cur);
                foreach (var e in evs) { foreach (string s in e.Set) cur.Add(s); foreach (string s in e.Clear) cur.Remove(s); }
            }
            list.Add(cur);
        }
        return list;
    }

    public string Describe() =>
        $"{Modified:MM-dd HH:mm:ss}  {NormalizeRoom(StartRoom)}  {(Map.Length > 0 ? Map : "?")}";
}

/// <summary>What the simulated camera did on one frame.</summary>
public sealed record SimFrame(PointF Camera, PointF Target, bool UsedRecorded, IReadOnlyList<BinElement> ActiveTriggers, string? Room);

/// <summary>
/// Re-plays a recording's player path against the CURRENT map and works out where the camera would be, with Celeste's own
/// camera rules: room bounds, room camera offset, cameraOffsetTrigger, cameraTargetTrigger, cameraAdvanceTargetTrigger,
/// everest/smoothCameraOffsetTrigger, killbox lower bound, red-dash/feather/summit-launch leads and the catch-up easing.
/// Edit a trigger and run it again to see the new camera without going back into the game. Player physics are not
/// re-simulated (the recorded path is used), and frames in cutscenes or rooms missing from the map fall back to the
/// recorded camera.
/// </summary>
public static class CameraSim
{
    public const float W = 320, H = 180;

    /// <summary>Trigger types the simulator understands.</summary>
    public static readonly string[] Supported =
        { "cameraOffsetTrigger", "cameraTargetTrigger", "cameraAdvanceTargetTrigger", "everest/smoothCameraOffsetTrigger" };

    public static bool IsCameraTrigger(string name) => name.Contains("camera", StringComparison.OrdinalIgnoreCase);

    public sealed class Result
    {
        public List<SimFrame> Frames = new();
        public List<HashSet<string>> Flags = new();
        /// <summary>Camera trigger types in the visited rooms that were ignored.</summary>
        public HashSet<string> Unsupported = new();
        /// <summary>Distance between simulated and recorded camera per frame (NaN without a recorded camera).</summary>
        public List<float> Divergence = new();
        public float MaxDivergence => Divergence.Where(d => !float.IsNaN(d)).DefaultIfEmpty(0).Max();
    }

    private static bool Bool(BinElement e, string k) => e.Find(k)?.Value switch
    {
        bool b => b, string s => s.Equals("true", StringComparison.OrdinalIgnoreCase), int i => i != 0, float f => f != 0, _ => false
    };

    private static float Clamp01(float v) => Math.Clamp(v, 0f, 1f);

    private static float ClampedMap(float v, float min, float max) => max == min ? 1 : Clamp01((v - min) / (max - min));

    /// <summary>Celeste's Trigger.GetPositionLerp.</summary>
    public static float PositionLerp(RectangleF trig, PointF playerCenter, string mode) => mode switch
    {
        "LeftToRight" => ClampedMap(playerCenter.X, trig.Left, trig.Right),
        "RightToLeft" => ClampedMap(playerCenter.X, trig.Right, trig.Left),
        "TopToBottom" => ClampedMap(playerCenter.Y, trig.Top, trig.Bottom),
        "BottomToTop" => ClampedMap(playerCenter.Y, trig.Bottom, trig.Top),
        "HorizontalCenter" => Math.Min(ClampedMap(playerCenter.X, trig.Left, trig.Left + trig.Width / 2), ClampedMap(playerCenter.X, trig.Right, trig.Right - trig.Width / 2)),
        "VerticalCenter" => Math.Min(ClampedMap(playerCenter.Y, trig.Top, trig.Top + trig.Height / 2), ClampedMap(playerCenter.Y, trig.Bottom, trig.Bottom - trig.Height / 2)),
        _ => 1f,
    };

    private static PointF FirstNode(Room room, BinElement t)
    {
        var n = t.Children.FirstOrDefault(c => c.Name == "node");
        return n == null ? new PointF(room.X + t.Num("x"), room.Y + t.Num("y")) : new PointF(room.X + n.Num("x"), room.Y + n.Num("y"));
    }

    private static bool Disabled(BinElement t, HashSet<string> flags) =>
        t.Str("deleteFlag") is { Length: > 0 } f && flags.Contains(f);

    /// <param name="overrides">Flags forced on (true) or off (false) for every frame, on top of the recorded flags.</param>
    public static Result Run(CameraRun run, MapBin map, IReadOnlyDictionary<string, bool>? overrides = null)
    {
        var res = new Result();
        var rooms = MapModel.Levels(map).Children.Select(l => new Room(l)).GroupBy(r => r.Name).ToDictionary(g => g.Key, g => g.First());
        var recFlags = run.FlagsPerFrame();
        var deaths = new HashSet<int>(run.Deaths);

        PointF cam = default, offset = default, anchor = default, anchorLerp = default;
        bool ignoreX = false, ignoreY = false, have = false;
        string? lastRoom = null;
        var inside = new HashSet<BinElement>();
        var killboxOn = new Dictionary<BinElement, bool>();

        for (int k = 0; k < run.Frames.Count; k++)
        {
            var f = run.Frames[k];
            var flags = recFlags[k];
            if (overrides is { Count: > 0 })
            {
                flags = new HashSet<string>(flags);
                foreach (var (name, on) in overrides) if (on) flags.Add(name); else flags.Remove(name);
            }
            res.Flags.Add(flags);
            string rn = CameraRun.NormalizeRoom(f.Room);
            rooms.TryGetValue(rn, out var room);

            // no map room, or a cutscene (dummy state): the game's own camera is the answer
            if (room == null || f.State == 11 || deaths.Contains(f.I))
            {
                if (f.Camera is { } rc) { cam = rc; have = true; }
                res.Frames.Add(new SimFrame(cam, cam, true, Array.Empty<BinElement>(), room?.Name));
                res.Divergence.Add(f.Camera is null ? float.NaN : 0);
                lastRoom = null;
                continue;
            }

            if (rn != lastRoom || !have)
            {
                // room entry: Celeste snaps the camera and loads the room's offset
                cam = f.Camera ?? new PointF(f.X - W / 2, f.Y - H / 2);
                offset = new PointF(room.El.Num("cameraOffsetX") * 48, room.El.Num("cameraOffsetY") * 32);
                anchorLerp = default; ignoreX = ignoreY = false;
                inside.Clear(); killboxOn.Clear();
                lastRoom = rn; have = true;
            }

            var hb = f.Hitbox;
            var center = new PointF(hb.X + hb.Width / 2, hb.Y + hb.Height / 2);
            var active = new List<BinElement>();
            var nowInside = new HashSet<BinElement>();
            foreach (var t in room.TriggersEl.Children)
            {
                if (!IsCameraTrigger(t.Name)) continue;
                var tr = MapModel.Box(room, t, true);
                if (tr.IntersectsWith(hb) && !Disabled(t, flags)) nowInside.Add(t);
            }

            // leaving the last target trigger releases the anchor
            bool targetLeft = inside.Any(t => !nowInside.Contains(t) && t.Name is "cameraTargetTrigger" or "cameraAdvanceTargetTrigger");
            if (targetLeft && !nowInside.Any(t => t.Name is "cameraTargetTrigger" or "cameraAdvanceTargetTrigger")) anchorLerp = default;

            foreach (var t in room.TriggersEl.Children)
            {
                if (!nowInside.Contains(t)) continue;
                active.Add(t);
                bool entered = !inside.Contains(t);
                var tr = MapModel.Box(room, t, true);
                switch (t.Name)
                {
                    case "cameraOffsetTrigger":
                        if (entered) offset = new PointF(t.Num("cameraX") * 48, t.Num("cameraY") * 32);
                        break;
                    case "cameraTargetTrigger":
                    {
                        var node = FirstNode(room, t);
                        anchor = new PointF(node.X - W / 2, node.Y - H / 2);
                        float l = Clamp01(t.Num("lerpStrength") * PositionLerp(tr, center, t.Str("positionMode", "NoEffect")));
                        anchorLerp = new PointF(l, l);
                        ignoreX = Bool(t, "yOnly"); ignoreY = Bool(t, "xOnly");
                        break;
                    }
                    case "cameraAdvanceTargetTrigger":
                    {
                        var node = FirstNode(room, t);
                        anchor = new PointF(node.X - W / 2, node.Y - H / 2);
                        anchorLerp = new PointF(Clamp01(t.Num("lerpStrengthX") * PositionLerp(tr, center, t.Str("positionModeX", "NoEffect"))),
                                                Clamp01(t.Num("lerpStrengthY") * PositionLerp(tr, center, t.Str("positionModeY", "NoEffect"))));
                        ignoreX = Bool(t, "yOnly"); ignoreY = Bool(t, "xOnly");
                        break;
                    }
                    case "everest/smoothCameraOffsetTrigger":
                    {
                        float p = PositionLerp(tr, center, t.Str("positionMode", "NoEffect"));
                        float ox = (t.Num("offsetXFrom") + (t.Num("offsetXTo") - t.Num("offsetXFrom")) * p) * 48;
                        float oy = (t.Num("offsetYFrom") + (t.Num("offsetYTo") - t.Num("offsetYFrom")) * p) * 32;
                        if (!Bool(t, "yOnly")) offset.X = ox;
                        if (!Bool(t, "xOnly")) offset.Y = oy;
                        break;
                    }
                    default:
                        res.Unsupported.Add(t.Name);
                        break;
                }
            }
            inside = nowInside;

            // Player.CameraTarget
            var target = new PointF(f.X - W / 2, f.Y - H / 2);
            if (f.State != 18) { target.X += offset.X; target.Y += offset.Y; }
            switch (f.State)
            {
                case 19: target.X += 0.2f * f.Vx; target.Y += 0.2f * f.Vy; break;          // feather
                case 5: target.X += 48 * Math.Sign(f.Vx); target.Y += 48 * Math.Sign(f.Vy); break; // red bubble dash
                case 10: target.Y -= 64; break;                                              // summit launch
                case 18: target.Y += 32; break;                                              // reflection fall
            }
            if (anchorLerp.X > 0 || anchorLerp.Y > 0)
            {
                if (ignoreX && !ignoreY) target.Y = target.Y + (anchor.Y - target.Y) * anchorLerp.Y;
                else if (!ignoreX && ignoreY) target.X = target.X + (anchor.X - target.X) * anchorLerp.X;
                else if (!ignoreX && !ignoreY)
                {
                    target.X += (anchor.X - target.X) * anchorLerp.X;
                    target.Y += (anchor.Y - target.Y) * anchorLerp.Y;
                }
            }
            var b = room.Bounds;
            target.X = Math.Clamp(target.X, b.Left, Math.Max(b.Left, b.Right - W));
            target.Y = Math.Clamp(target.Y, b.Top, Math.Max(b.Top, b.Bottom - H));

            foreach (var e in room.EntitiesEl.Children)
            {
                if (e.Name != "killbox") continue;
                var kb = new RectangleF(room.X + e.Num("x"), room.Y + e.Num("y"), Math.Max(8, e.Num("width")), 32);
                killboxOn.TryGetValue(e, out bool on);
                if (!on && hb.Bottom < kb.Top - 32) on = true;
                else if (on && hb.Top > kb.Bottom + 32) on = false;
                killboxOn[e] = on;
                if (on && hb.Top < kb.Bottom && hb.Right > kb.Left && hb.Left < kb.Right) target.Y = Math.Min(target.Y, kb.Top - H);
            }

            float mult = f.State == 20 ? 8 : 1;
            float factor = 1 - MathF.Pow(0.01f / mult, f.Dt > 0 ? f.Dt : 1f / 60);
            cam = new PointF(cam.X + (target.X - cam.X) * factor, cam.Y + (target.Y - cam.Y) * factor);

            res.Frames.Add(new SimFrame(cam, target, false, active, room.Name));
            res.Divergence.Add(f.Camera is { } rec ? MathF.Sqrt((rec.X - cam.X) * (rec.X - cam.X) + (rec.Y - cam.Y) * (rec.Y - cam.Y)) : float.NaN);
        }
        return res;
    }

    /// <summary>Flags that camera triggers in <paramref name="room"/> read (deleteFlag and MaxHelpingHand flag toggles).</summary>
    public static SortedSet<string> CameraFlags(Room room)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var t in room.TriggersEl.Children)
        {
            if (!IsCameraTrigger(t.Name)) continue;
            foreach (var a in t.Attrs)
                if (MapSearch.IsFlagAttr(a.Key))
                    foreach (var (fl, _) in MapSearch.SplitFlags(Convert.ToString(a.Value, CultureInfo.InvariantCulture) ?? "")) set.Add(fl);
        }
        return set;
    }
}
