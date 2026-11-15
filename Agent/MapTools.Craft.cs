using System.Text.Json;
using System.Text.Json.Nodes;

namespace CELMapViewer.Agent;

/// <summary>
/// What the agent picked from the palette and placed before, across maps and sessions: how often, on which surface,
/// with which attributes, plus notes it left for itself. Kept in %APPDATA%\Topo\ai_placements.json.
/// </summary>
public static class PlacementMemory
{
    private const int MaxPicks = 400, MaxNotes = 100;
    private static readonly object Gate = new();
    private static string? _file;
    private static JsonObject? _root;

    /// <summary>Set to keep the memory elsewhere (tests), null for the default file.</summary>
    public static string? FilePathOverride
    {
        get => _file;
        set { lock (Gate) { _file = value; _root = null; } }
    }

    public static string FilePath => _file ?? Path.Combine(AppInfo.DataDir, "ai_placements.json");

    private static JsonObject Root()
    {
        if (_root != null) return _root;
        try
        {
            if (File.Exists(FilePath) && JsonNode.Parse(File.ReadAllText(FilePath)) is JsonObject o) return _root = o;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { /* damaged or locked: start empty */ }
        return _root = new JsonObject();
    }

    private static JsonObject Obj(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject o) return o;
        o = new JsonObject();
        parent[key] = o;
        return o;
    }

    // Counters are ints when set in this session and JSON numbers after a reload; read both.
    private static int Int(JsonNode? n) =>
        n is not JsonValue v ? 0 : v.TryGetValue(out int i) ? i : v.TryGetValue(out double d) ? (int)d : 0;

    public static string Key(string type, string? placement) => string.IsNullOrEmpty(placement) ? type : type + " | " + placement;

    /// <summary>Notes one placement. Call <see cref="Save"/> once after a tool's placements.</summary>
    public static void Record(string type, string? placement, bool trigger, string surface, JsonObject? attrs, string? map)
    {
        lock (Gate)
        {
            var picks = Obj(Root(), "picks");
            string key = Key(type, placement);
            if (picks[key] is not JsonObject p)
            {
                p = new JsonObject { ["type"] = type };
                if (!string.IsNullOrEmpty(placement)) p["placement"] = placement;
                if (trigger) p["trigger"] = true;
                picks[key] = p;
            }
            p["count"] = Int(p["count"]) + 1;
            p["last"] = DateTime.Now.ToString("yyyy-MM-dd");
            var on = Obj(p, "on");
            on[surface] = Int(on[surface]) + 1;
            if (attrs is { Count: > 0 }) p["attrs"] = attrs.DeepClone();
            if (!string.IsNullOrEmpty(map))
            {
                var maps = (p["maps"] as JsonArray)?.Select(m => m?.ToString() ?? "").Where(m => m != map).ToList() ?? new List<string>();
                maps.Add(map);
                p["maps"] = new JsonArray(maps.TakeLast(5).Select(m => (JsonNode)m).ToArray());
            }
            if (picks.Count > MaxPicks)
                foreach (string old in picks.OrderBy(kv => Int(kv.Value?["count"])).ThenBy(kv => kv.Value?["last"]?.ToString()).Take(picks.Count - MaxPicks).Select(kv => kv.Key).ToList())
                    picks.Remove(old);
        }
    }

    public static void Save()
    {
        lock (Gate)
        {
            if (_root == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, _root.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
                }));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* memory is a convenience, not critical */ }
        }
    }

    /// <summary>The picks matching a query (type, placement or note), most used or most recent first.</summary>
    public static JsonObject Recall(string query, int limit, bool recent)
    {
        lock (Gate)
        {
            var root = Root();
            var picks = (root["picks"] as JsonObject)?.Where(kv => kv.Value is JsonObject).Select(kv => (kv.Key, P: (JsonObject)kv.Value!)).ToList() ?? new();
            if (query.Length > 0)
                picks = picks.Where(x => x.Key.Contains(query, StringComparison.OrdinalIgnoreCase) || (x.P["note"]?.ToString() ?? "").Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            var ordered = recent
                ? picks.OrderByDescending(x => x.P["last"]?.ToString()).ThenByDescending(x => Int(x.P["count"]))
                : picks.OrderByDescending(x => Int(x.P["count"])).ThenByDescending(x => x.P["last"]?.ToString());
            var notes = (root["notes"] as JsonArray)?.Where(n => query.Length == 0 || (n?["text"]?.ToString() ?? "").Contains(query, StringComparison.OrdinalIgnoreCase)).ToList() ?? new();
            return new JsonObject
            {
                ["total_picks"] = picks.Count,
                ["picks"] = new JsonArray(ordered.Take(limit).Select(x => x.P.DeepClone()).ToArray()),
                ["notes"] = new JsonArray(notes.TakeLast(limit).Select(n => n?.DeepClone()).ToArray()),
                ["file"] = FilePath
            };
        }
    }

    public static string Note(string? type, string? placement, string text, string? map)
    {
        lock (Gate)
        {
            var root = Root();
            if (!string.IsNullOrEmpty(type))
            {
                var picks = Obj(root, "picks");
                string key = Key(type, placement);
                // a note on a type applies to its plain entry unless a placement is named
                if (picks[key] is not JsonObject p)
                {
                    p = new JsonObject { ["type"] = type, ["count"] = 0 };
                    if (!string.IsNullOrEmpty(placement)) p["placement"] = placement;
                    picks[key] = p;
                }
                p["note"] = text;
                Save();
                return $"Noted on {key}.";
            }
            if (root["notes"] is not JsonArray notes) root["notes"] = notes = new JsonArray();
            var n = new JsonObject { ["date"] = DateTime.Now.ToString("yyyy-MM-dd"), ["text"] = text };
            if (!string.IsNullOrEmpty(map)) n["map"] = map;
            notes.Add(n);
            while (notes.Count > MaxNotes) notes.RemoveAt(0);
            Save();
            return "Noted.";
        }
    }

    public static string Forget(string? type, bool all)
    {
        lock (Gate)
        {
            var root = Root();
            if (all)
            {
                root.Clear();
                Save();
                return "Placement memory cleared.";
            }
            if (string.IsNullOrEmpty(type)) return "Give a type to forget, or all=true.";
            var picks = Obj(root, "picks");
            var keys = picks.Select(kv => kv.Key).Where(k => k == type || k.StartsWith(type + " | ", StringComparison.Ordinal)).ToList();
            foreach (string k in keys) picks.Remove(k);
            Save();
            return keys.Count == 0 ? $"Nothing remembered about '{type}'." : $"Forgot {string.Join(", ", keys)}.";
        }
    }

    /// <summary>One line for the agent's per-turn context, or null when nothing was placed yet.</summary>
    public static string? Summary(int top = 8)
    {
        lock (Gate)
        {
            var root = Root();
            var picks = (root["picks"] as JsonObject)?.Where(kv => kv.Value is JsonObject && Int(kv.Value["count"]) > 0)
                .OrderByDescending(kv => Int(kv.Value!["count"])).Take(top).ToList() ?? new();
            int notes = (root["notes"] as JsonArray)?.Count ?? 0;
            if (picks.Count == 0 && notes == 0) return null;
            string line = string.Join(", ", picks.Select(kv =>
            {
                string surface = (kv.Value!["on"] as JsonObject)?.OrderByDescending(s => Int(s.Value)).Select(s => s.Key).FirstOrDefault() ?? "free";
                return $"{kv.Key} ×{Int(kv.Value["count"])}{(surface == "free" ? "" : $" ({surface})")}";
            }));
            return $"Placement memory (placement_memory recall for attrs and notes): {line}{(notes > 0 ? $"; {notes} note(s)" : "")}.";
        }
    }
}

// Hand-crafting tools: set an item down on a surface like a person would, remember what was picked and placed,
// and save/reuse preset groups (the same presets as the editor's Presets menu).
public sealed partial class MapTools
{
    private static JsonObject RestP => Enum("Set the item down like a hand would: slide it from x,y until it touches solid fg tiles in this direction " +
        "(floor = down, ceiling = up, left/right = onto that wall). Spikes, springs, spawns and blocks then sit exactly on the surface.", "floor", "ceiling", "left", "right");

    private void RegisterCraft()
    {
        Add("placement_memory", "Your memory of hand-placed items, kept across maps and sessions (%APPDATA%\\Topo\\ai_placements.json). Every add_entity, build_room entity " +
            "and place_preset is recorded automatically: which type/placement you picked, how often, on which surface (floor/ceiling/left/right/free), the attributes you gave it " +
            "and the maps it went into. action recall lists the picks (most used first, or sort=recent) and your notes, optionally filtered by query; " +
            "note stores a remark on a type ('needs 16px headroom', 'the user prefers the dust variant') or, without type, a general lesson; forget drops a type (or all=true). " +
            "Recall before decorating or adding hazards so you reuse what worked, and leave a note when the user corrects a placement.", true,
            Props(("action", Enum("What to do.", "recall", "note", "forget")),
                  ("query", P("string", "recall: substring of a type, placement or note.")),
                  ("sort", Enum("recall order (default used).", "used", "recent")),
                  ("limit", P("integer", "recall: max picks and notes (default 25).")),
                  ("type", P("string", "note/forget: entity or trigger type.")),
                  ("placement", P("string", "note: placement name of that type, when the note is about one variant.")),
                  ("text", P("string", "note: the remark.")),
                  ("all", P("boolean", "forget: clear the whole memory."))), new[] { "action" }, MemoryTool);

        Add("list_presets", "Saved presets (single entities/triggers) and preset groups (several entities, triggers and decals with their relative positions): the same ones as " +
            "the editor's Presets menu, shared by the user and you. Shows each preset's items and size.", true,
            Props(("query", P("string", "Substring of the preset name or of an item type.")),
                  ("kind", Enum("Filter.", "all", "group", "entity", "trigger"))), None, ListPresets);
        Add("save_preset", "Save items of a room as a preset group for later reuse (it also appears in the editor's Presets menu). Pick them by ids and/or by an area " +
            "(room-local px; every entity, trigger and decal whose box touches it, player spawns excepted). Positions are stored relative to the group's top-left. " +
            "Save an arrangement worth keeping (a hazard pattern, a puzzle unit, a decorated corner) before reworking it, just in case, and whenever it will be reused.", true,
            Props(("name", P("string", "Preset name.")), ("room", RoomP),
                  ("ids", ArrayP("Entity/trigger ids to include.", new JsonObject { ["type"] = "integer" })),
                  ("area", P("object", "{x, y, width, height} in room-local px.")),
                  ("decals", P("boolean", "Include decals inside the area (default true).")),
                  ("group", P("boolean", "Save as a preset group even when it is a single item (default true).")),
                  ("overwrite", P("boolean", "Replace an existing preset of the same name (default false)."))), new[] { "name", "room" }, SavePreset);
        Add("place_preset", "Place a saved preset or preset group in a room with its top-left at x,y (room-local px, snapped to the 8px grid unless snap=false). " +
            "Items get fresh ids and show up selected in the editor.", false,
            Props(("name", P("string", "Preset name (see list_presets).")), ("room", RoomP),
                  ("x", P("number", "Room-local x of the group's top-left.")), ("y", P("number", "Room-local y of the group's top-left.")),
                  ("kind", Enum("Only needed when a group and a single preset share the name.", "group", "entity", "trigger")),
                  ("snap", P("boolean", "Snap to the 8px grid (default true)."))), new[] { "name", "room", "x", "y" }, PlacePreset);
        Add("delete_preset", "Delete a saved preset or preset group by name.", false,
            Props(("name", P("string", "Preset name.")), ("kind", Enum("Only needed when presets of different kinds share the name.", "group", "entity", "trigger"))),
            new[] { "name" }, DeletePreset);
    }

    // ---- setting items down ----

    /// <summary>Room-local box of an item; sprites are measured directly so the result doesn't depend on what the canvas has loaded.</summary>
    private static RectangleF ItemBox(Room r, BinElement el, bool trigger)
    {
        float x = el.Num("x"), y = el.Num("y");
        if (!trigger && MapModel.IsSpawn(el)) return new RectangleF(x - 4, y - 11, 8, 11);
        if (!trigger && el.Num("width") <= 0 && el.Num("height") <= 0 && EntityVisuals.SpikeDir(el.Name) == null
            && EntityVisuals.SpriteFor(el) is { } s && GameAssets.Gameplay.GetFirstFrame(s.Path) is { } tex)
            return new RectangleF(x - tex.W * s.Justify.X, y - tex.H * s.Justify.Y, tex.W, tex.H);
        return Local(r, MapModel.Box(r, el, trigger));
    }

    /// <summary>Slides an item from where it is until its box touches solid fg tiles on the given side. Returns the canonical surface name and a description.</summary>
    private static (string Surface, string Text) RestOn(Room r, BinElement el, bool trigger, string rest)
    {
        string surface = rest.Trim().ToLowerInvariant() switch
        {
            "floor" or "ground" or "down" or "bottom" => "floor",
            "ceiling" or "up" or "top" => "ceiling",
            "left" or "left_wall" => "left",
            "right" or "right_wall" => "right",
            _ => throw new ToolException("rest must be floor, ceiling, left or right.")
        };
        bool vertical = surface is "floor" or "ceiling";
        int dir = surface is "floor" or "right" ? 1 : -1;
        var g = r.Grid(true);
        bool Solid(int cx, int cy) => (uint)cx < (uint)g.Cols && (uint)cy < (uint)g.Rows && g[cx, cy] != '0';

        var box = ItemBox(r, el, trigger);
        float ox = el.Num("x"), oy = el.Num("y");
        // Sized items (blocks, spike strips, platforms) need contact anywhere along their edge; point items are judged at their origin.
        bool sized = trigger || el.Num("width") > 0 || el.Num("height") > 0 || EntityVisuals.SpikeDir(el.Name) != null;
        float a0 = vertical ? (sized ? box.Left : ox) : (sized ? box.Top : oy);
        float a1 = vertical ? (sized ? box.Right : ox + 1) : (sized ? box.Bottom : oy + 1);
        int c0 = (int)MathF.Floor(a0 / 8), c1 = (int)MathF.Floor((a1 - 0.01f) / 8);
        bool Hit(int line)
        {
            for (int c = c0; c <= c1; c++)
                if (vertical ? Solid(c, line) : Solid(line, c)) return true;
            return false;
        }

        int limit = vertical ? g.Rows : g.Cols;
        float lead = dir > 0 ? (vertical ? box.Bottom : box.Right) : (vertical ? box.Top : box.Left);
        int line = Math.Clamp((int)MathF.Floor((dir > 0 ? lead - 0.01f : lead) / 8), 0, limit - 1);
        if (Hit(line))
        {
            // the point is inside the surface: back out to its face
            while (line >= 0 && line < limit && Hit(line)) line -= dir;
            if (line < 0 || line >= limit) throw new ToolException($"({ox},{oy}) in {r.Name} is inside solid tiles all the way to the room edge; pick a point in open space.");
            line += dir;
        }
        else
        {
            while (line >= 0 && line < limit && !Hit(line)) line += dir;
            if (line < 0 || line >= limit)
                throw new ToolException($"No solid tiles {(surface switch { "floor" => "below", "ceiling" => "above", "left" => "left of", _ => "right of" })} ({ox},{oy}) in {r.Name} to rest on.");
        }
        float face = dir > 0 ? line * 8 : (line + 1) * 8;
        float d = face - lead;
        if (vertical) MapCanvas.SetNum(el, "y", oy + d);
        else MapCanvas.SetNum(el, "x", ox + d);
        return (surface, $"set down on the {(vertical ? surface : surface + " wall")} at ({el.Num("x")},{el.Num("y")}), moved {Math.Abs(d):0.#}px");
    }

    /// <summary>The editor's Place tool grid: rectangles sit on the cell under the point, everything else rounds to 8px.</summary>
    private static void SnapLikeEditor(BinElement el, bool trigger)
    {
        float x = el.Num("x"), y = el.Num("y");
        if (trigger || el.Int("width") > 0 || el.Int("height") > 0) { x = MathF.Floor(x / 8) * 8; y = MathF.Floor(y / 8) * 8; }
        else { x = MapModel.Snap8(x); y = MapModel.Snap8(y); }
        MapCanvas.SetNum(el, "x", x);
        MapCanvas.SetNum(el, "y", y);
    }

    /// <summary>Applies the hand-placement options of a spec (snap, rest) to a new item. Returns the surface it rests on ("free" when none) and a note for the summary.</summary>
    private static (string Surface, string Note) HandPlace(Room r, BinElement el, bool trigger, JsonObject spec)
    {
        if (B(spec, "snap")) SnapLikeEditor(el, trigger);
        if (S(spec, "rest") is not { Length: > 0 } rest || rest.Equals("none", StringComparison.OrdinalIgnoreCase)) return ("free", "");
        var (surface, text) = RestOn(r, el, trigger, rest);
        return (surface, text);
    }

    private string? MapLabel() => _host.Path is { } p ? System.IO.Path.GetFileNameWithoutExtension(p) : null;

    private void Remember(BinElement el, bool trigger, JsonObject spec, string surface)
    {
        if (MapModel.IsSpawn(el)) return;
        var attrs = (spec["attrs"] as JsonObject)?.DeepClone() as JsonObject ?? new JsonObject();
        foreach (string k in new[] { "width", "height" })
            if (el.Find(k) != null && !attrs.ContainsKey(k)) attrs[k] = el.Int(k);
        PlacementMemory.Record(el.Name, S(spec, "placement"), trigger, surface, attrs, MapLabel());
    }

    /// <summary>Selects freshly placed items in the editor window (nothing happens headless or inside a batch).</summary>
    private void Reveal(BinElement? level, IReadOnlyList<BinElement> items)
    {
        if (level == null || items.Count == 0 || _batchDepth > 0) return;
        try { _host.Invoke(() => { _host.Reveal(level, items); return 0; }); }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException) { /* the window is closing; the edit itself is done */ }
    }

    // ---- memory tool ----

    private ToolResult MemoryTool(JsonObject a)
    {
        switch (Req(a, "action").ToLowerInvariant())
        {
            case "recall":
                return ToolResult.Json(PlacementMemory.Recall(S(a, "query") ?? "", Math.Clamp(I(a, "limit") ?? 25, 1, 400), S(a, "sort") == "recent"));
            case "note":
                string text = Req(a, "text");
                return ToolResult.Text(PlacementMemory.Note(S(a, "type"), S(a, "placement"), text, _host.Invoke(MapLabel)));
            case "forget":
                return ToolResult.Text(PlacementMemory.Forget(S(a, "type"), B(a, "all")));
            default:
                throw new ToolException("action must be recall, note or forget.");
        }
    }

    // ---- presets ----

    private static JsonObject PresetJson(Preset p)
    {
        var items = PresetStore.Items(p) ?? new();
        var arr = new JsonArray();
        float w = 0, h = 0;
        foreach (var (layer, el) in items)
        {
            bool decal = layer.EndsWith("decal");
            var o = new JsonObject { ["layer"] = layer, ["name"] = decal ? el.Str("texture") : el.Name, ["x"] = el.Num("x"), ["y"] = el.Num("y") };
            if (el.Num("width") > 0) o["width"] = el.Num("width");
            if (el.Num("height") > 0) o["height"] = el.Num("height");
            arr.Add(o);
            w = Math.Max(w, el.Num("x") + Math.Max(8, el.Num("width")));
            h = Math.Max(h, el.Num("y") + Math.Max(8, el.Num("height")));
        }
        return new JsonObject { ["name"] = p.Name, ["kind"] = p.Kind, ["count"] = items.Count, ["approx_size"] = $"{w:0}x{h:0}", ["items"] = arr };
    }

    private static Preset FindPreset(string name, string? kind)
    {
        var matches = PresetStore.All.Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
                                                 && (string.IsNullOrEmpty(kind) || p.Kind == kind)).ToList();
        if (matches.Count == 0) throw new ToolException($"No preset '{name}'{(string.IsNullOrEmpty(kind) ? "" : $" of kind {kind}")}. Use list_presets.");
        return matches.FirstOrDefault(p => p.Kind == "group") ?? matches[0];
    }

    private ToolResult ListPresets(JsonObject a) => _host.Invoke(() =>
    {
        string q = S(a, "query") ?? "", kind = S(a, "kind") ?? "all";
        var arr = new JsonArray();
        foreach (var p in PresetStore.All.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (kind != "all" && p.Kind != kind) continue;
            var json = PresetJson(p);
            if (q.Length > 0 && !p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !((JsonArray)json["items"]!).Any(i => (i?["name"]?.ToString() ?? "").Contains(q, StringComparison.OrdinalIgnoreCase))) continue;
            arr.Add(json);
        }
        return ToolResult.Json(new JsonObject { ["total"] = arr.Count, ["presets"] = arr, ["file"] = PresetStore.FilePath });
    });

    private ToolResult SavePreset(JsonObject a) => _host.Invoke(() =>
    {
        string name = Req(a, "name").Trim();
        var r = FindRoom(Req(a, "room"));
        var ids = new HashSet<int>();
        if (a["ids"] is JsonArray idArr)
            foreach (var n in idArr)
                if (n is JsonValue v && v.TryGetValue(out double d)) ids.Add((int)d);
        RectangleF? area = a["area"] is JsonObject ar
            ? new RectangleF((float)(D(ar, "x") ?? 0), (float)(D(ar, "y") ?? 0), (float)(D(ar, "width") ?? D(ar, "w") ?? 0), (float)(D(ar, "height") ?? D(ar, "h") ?? 0))
            : null;
        if (ids.Count == 0 && area == null) throw new ToolException("Give ids (entity/trigger ids of the room) and/or area {x, y, width, height} in room-local px.");
        if (area is { } q0 && (q0.Width <= 0 || q0.Height <= 0)) throw new ToolException("area needs a positive width and height.");

        var picked = new List<(string Layer, BinElement El)>();
        var found = new HashSet<int>();
        foreach (var (list, layer, trigger) in new[] { (r.EntitiesEl, "entity", false), (r.TriggersEl, "trigger", true) })
            foreach (var e in list.Children)
            {
                bool byId = ids.Contains(e.Int("id"));
                bool byArea = area is { } q && !MapModel.IsSpawn(e) && q.IntersectsWith(ItemBox(r, e, trigger));
                if (!byId && !byArea) continue;
                if (byId) found.Add(e.Int("id"));
                picked.Add((layer, e));
            }
        if (area is { } qa && B(a, "decals", true))
            foreach (var (listName, layer) in new[] { ("bgdecals", "bgdecal"), ("fgdecals", "fgdecal") })
                foreach (var d in r.El.Child(listName)?.Children ?? new())
                    if (qa.IntersectsWith(Local(r, MapCanvas.DecalBox(r, d)))) picked.Add((layer, d));
        var missing = ids.Except(found).ToList();
        if (missing.Count > 0) throw new ToolException($"No entity or trigger with id {string.Join(", ", missing)} in room {r.Name}.");
        if (picked.Count == 0) throw new ToolException($"Nothing to save: no items in that area of {r.Name}.");

        var preset = PresetStore.Make(name, picked, B(a, "group", true));
        if (!B(a, "overwrite") && PresetStore.All.Any(p => p.Kind == preset.Kind && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ToolException($"A {preset.Kind} preset named '{name}' already exists. Pick another name or pass overwrite=true.");
        PresetStore.Add(preset);
        string what = string.Join(", ", picked.GroupBy(i => i.Layer.EndsWith("decal") ? i.Layer : i.El.Name).Select(g => $"{g.Count()} {g.Key}"));
        string msg = $"Saved {(preset.Kind == "group" ? "preset group" : preset.Kind + " preset")} '{preset.Name}' from {r.Name}: {picked.Count} item(s) ({what}). " +
                     "It is in the editor's Presets menu; place it with place_preset.";
        _host.Log("[AI] " + msg);
        return ToolResult.Text(msg);
    });

    private ToolResult PlacePreset(JsonObject a)
    {
        var placed = new List<BinElement>();
        BinElement? level = null;
        var res = Edit(() =>
        {
            var map = MapOrThrow();
            var r = FindRoom(Req(a, "room"));
            var preset = FindPreset(Req(a, "name"), S(a, "kind"));
            var items = PresetStore.Items(preset);
            if (items == null || items.Count == 0) throw new ToolException($"Preset '{preset.Name}' is empty or damaged.");
            float x = (float)(D(a, "x") ?? 0), y = (float)(D(a, "y") ?? 0);
            if (B(a, "snap", true)) { x = MapModel.Snap8(x); y = MapModel.Snap8(y); }
            float dx = x - items.Min(i => i.El.Num("x")), dy = y - items.Min(i => i.El.Num("y"));
            int id = MapModel.NextId(map), first = id, outside = 0;
            foreach (var (layer, el) in items)
            {
                PresetStore.Shift(el, dx, dy);
                bool decal = layer.EndsWith("decal");
                if (!decal) el.Set("id", id++);
                var container = layer switch
                {
                    "trigger" or "triggers" => r.TriggersEl,
                    "fgdecal" => r.GetOrAdd("fgdecals"),
                    "bgdecal" => r.GetOrAdd("bgdecals"),
                    _ => r.EntitiesEl
                };
                container.Children.Add(el);
                placed.Add(el);
                if (el.Num("x") < -8 || el.Num("y") < -8 || el.Num("x") > r.W + 8 || el.Num("y") > r.H + 8) outside++;
            }
            level = r.El;
            PlacementMemory.Record("preset:" + preset.Name, null, false, "free", null, MapLabel());
            string idText = id == first ? "" : id - first == 1 ? $", id {first}" : $", ids {first}-{id - 1}";
            return (r.El, $"Placed preset '{preset.Name}' ({items.Count} item(s)) with its top-left at ({x},{y}) in {r.Name}{idText}." +
                          (outside > 0 ? $" Warning: {outside} item(s) ended up outside the room ({r.W}x{r.H})." : ""));
        });
        PlacementMemory.Save();
        Reveal(level, placed);
        return res;
    }

    private ToolResult DeletePreset(JsonObject a) => _host.Invoke(() =>
    {
        var preset = FindPreset(Req(a, "name"), S(a, "kind"));
        PresetStore.All.Remove(preset);
        PresetStore.Save();
        string msg = $"Deleted {preset.Kind} preset '{preset.Name}'.";
        _host.Log("[AI] " + msg);
        return ToolResult.Text(msg);
    });
}
