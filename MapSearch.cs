namespace CELMapViewer;

public enum SearchKind { All, Entities, Triggers, Decals, Rooms }

/// <summary>What part of an item the query is matched against.</summary>
public enum SearchField
{
    /// <summary>Type/texture names, room names, attribute names and values.</summary>
    Anything,
    /// <summary>Only entity/trigger type names, decal textures and room names.</summary>
    Names,
    /// <summary>Only attribute values.</summary>
    Values,
    /// <summary>Only attributes that hold session flags (flag, flags, deleteFlag, requiredFlag, ...); values are split into single flags.</summary>
    Flags,
    /// <summary>Only attributes that hold dialog keys (dialog, dialogId, ...).</summary>
    Dialog,
}

public sealed record SearchOptions(SearchKind Kind = SearchKind.All, SearchField Field = SearchField.Anything,
                                   bool WholeWord = false, bool CaseSensitive = false, string? Room = null);

/// <summary>One match. <see cref="El"/> is null for a room match.</summary>
public sealed record SearchHit(Room Room, BinElement? El, SearchKind Kind, string Name, string Field, string Value)
{
    public bool Trigger => Kind == SearchKind.Triggers;
    public bool Decal => Kind == SearchKind.Decals;
    public string KindLabel => Kind switch
    {
        SearchKind.Entities => "Entity", SearchKind.Triggers => "Trigger", SearchKind.Decals => El?.Name == "fgdecal" ? "FG decal" : "Decal",
        _ => "Room"
    };

    /// <summary>World position of the item (room top-left for a room hit).</summary>
    public PointF World => El == null ? new PointF(Room.X, Room.Y) : new PointF(Room.X + El.Num("x"), Room.Y + El.Num("y"));

    public Selection ToSelection() => El == null ? Selection.OfRoom(Room) : new Selection(Room, El, Trigger, Decal);
}

/// <summary>One place a session flag is used, and whether that place sets it or only reads it.</summary>
public sealed record FlagUse(string Flag, SearchHit Hit, bool Sets, bool Inverted);

/// <summary>
/// Find placed entities, triggers, decals and rooms by name or attribute (flags, dialog keys, any value), Topo's
/// counterpart to "find and jump to" tools for Lönn. Pure data: the editor's Find window and the agent tools share it.
/// </summary>
public static class MapSearch
{
    public static IEnumerable<SearchHit> Find(MapBin map, string query, SearchOptions? options = null)
    {
        var o = options ?? new SearchOptions();
        query = query.Trim();
        // an empty query lists everything of that field (e.g. every flag in the map)
        bool all = query.Length == 0;
        if (all && o.Field is SearchField.Anything or SearchField.Names or SearchField.Values && o.Kind == SearchKind.All) yield break;

        string? onlyRoom = o.Room is { Length: > 0 } rn ? (rn.StartsWith("lvl_") ? rn[4..] : rn) : null;
        foreach (var l in MapModel.Levels(map).Children)
        {
            var room = new Room(l);
            if (onlyRoom != null && !string.Equals(room.Name, onlyRoom, StringComparison.OrdinalIgnoreCase)) continue;

            if (o.Kind is SearchKind.All or SearchKind.Rooms)
                foreach (var h in MatchItem(room, null, SearchKind.Rooms, room.Name, l, query, all, o)) yield return h;

            if (o.Kind is SearchKind.All or SearchKind.Entities)
                foreach (var e in l.Child("entities")?.Children ?? new())
                    foreach (var h in MatchItem(room, e, SearchKind.Entities, e.Name, e, query, all, o)) yield return h;

            if (o.Kind is SearchKind.All or SearchKind.Triggers)
                foreach (var t in l.Child("triggers")?.Children ?? new())
                    foreach (var h in MatchItem(room, t, SearchKind.Triggers, t.Name, t, query, all, o)) yield return h;

            if (o.Kind is SearchKind.All or SearchKind.Decals)
                foreach (string list in new[] { "fgdecals", "bgdecals" })
                    foreach (var d in l.Child(list)?.Children ?? new())
                        foreach (var h in MatchItem(room, d, SearchKind.Decals, d.Str("texture"), d, query, all, o)) yield return h;
        }
    }

    // a room's own attributes that are not worth matching (tile grids, sizes, positions)
    private static readonly HashSet<string> SkipAttrs = new(StringComparer.OrdinalIgnoreCase)
    {
        "innerText", "x", "y", "width", "height", "id", "originX", "originY", "offsetX", "offsetY", "tileset", "exportMode"
    };

    private static IEnumerable<SearchHit> MatchItem(Room room, BinElement? el, SearchKind kind, string name, BinElement attrsOf,
                                                    string query, bool all, SearchOptions o)
    {
        bool nameDone = false;
        if (o.Field is SearchField.Anything or SearchField.Names)
        {
            if (all || Matches(name, query, o))
            {
                nameDone = true;
                yield return new SearchHit(room, el, kind, name, kind == SearchKind.Decals ? "texture" : "name", name);
            }
        }
        if (o.Field == SearchField.Names) yield break;

        foreach (var a in attrsOf.Attrs)
        {
            if (SkipAttrs.Contains(a.Key) || (kind == SearchKind.Decals && a.Key == "texture") || (el == null && a.Key == "name")) continue;
            string value = Convert.ToString(a.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
            switch (o.Field)
            {
                case SearchField.Flags:
                    if (!IsFlagAttr(a.Key)) continue;
                    foreach (var (flag, _) in SplitFlags(value))
                        if (all || Matches(flag, query, o)) yield return new SearchHit(room, el, kind, name, a.Key, flag);
                    continue;
                case SearchField.Dialog:
                    if (!IsDialogAttr(a.Key) || value.Length == 0) continue;
                    if (all || Matches(value, query, o)) yield return new SearchHit(room, el, kind, name, a.Key, value);
                    continue;
                case SearchField.Values:
                    if (all || Matches(value, query, o)) yield return new SearchHit(room, el, kind, name, a.Key, value);
                    continue;
                default: // Anything: an attribute name or value
                    if (nameDone) continue;
                    if (Matches(value, query, o) || Matches(a.Key, query, o)) yield return new SearchHit(room, el, kind, name, a.Key, value);
                    continue;
            }
        }
    }

    public static bool Matches(string text, string query, SearchOptions o)
    {
        var cmp = o.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (!o.WholeWord) return text.Contains(query, cmp);
        int at = 0;
        while ((at = text.IndexOf(query, at, cmp)) >= 0)
        {
            bool left = at == 0 || !IsWordChar(text[at - 1]);
            int end = at + query.Length;
            bool right = end >= text.Length || !IsWordChar(text[end]);
            if (left && right) return true;
            at++;
        }
        return false;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>An attribute that names session flags: flag, flags, deleteFlag, requiredFlags, flagIfFlag, ... (not "flip").</summary>
    public static bool IsFlagAttr(string key) => key.Contains("flag", StringComparison.OrdinalIgnoreCase);

    public static bool IsDialogAttr(string key) => key.Contains("dialog", StringComparison.OrdinalIgnoreCase);

    /// <summary>Splits a flag attribute value ("a,!b, c") into flags; a leading '!' means inverted.</summary>
    public static IEnumerable<(string Flag, bool Inverted)> SplitFlags(string value)
    {
        foreach (string part in value.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            bool inv = part.StartsWith('!');
            string f = inv ? part[1..].Trim() : part;
            if (f.Length > 0 && !bool.TryParse(f, out _) && !double.TryParse(f, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
                yield return (f, inv);
        }
    }

    // attribute names that read a flag as a condition; anything else on a flag-setting type sets it
    private static bool IsConditionAttr(string key) =>
        key.Contains("require", StringComparison.OrdinalIgnoreCase) || key.Contains("ifFlag", StringComparison.OrdinalIgnoreCase)
        || key.Contains("delete", StringComparison.OrdinalIgnoreCase) || key.Contains("only", StringComparison.OrdinalIgnoreCase)
        || key.Contains("when", StringComparison.OrdinalIgnoreCase) || key.Contains("input", StringComparison.OrdinalIgnoreCase)
        || key.Contains("condition", StringComparison.OrdinalIgnoreCase) || key.Contains("enable", StringComparison.OrdinalIgnoreCase)
        || key.Contains("disable", StringComparison.OrdinalIgnoreCase) || key.Contains("active", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every flag in the map and where it is used. "Sets" is a best guess from the type and attribute names
    /// (flag triggers, switches, touch switches, flag-if-flag outputs set; required/delete/if flags read).
    /// </summary>
    public static List<FlagUse> Flags(MapBin map, string? room = null)
    {
        var list = new List<FlagUse>();
        foreach (var h in Find(map, "", new SearchOptions(Field: SearchField.Flags, Room: room)))
        {
            string type = h.Name;
            bool setterType = type.Contains("flag", StringComparison.OrdinalIgnoreCase) && (type.Contains("trigger", StringComparison.OrdinalIgnoreCase)
                              || type.Contains("FlagIfFlag") || type.Contains("LogicGate") || type.Contains("Toggle", StringComparison.OrdinalIgnoreCase))
                              || type.Contains("switch", StringComparison.OrdinalIgnoreCase) || type.Contains("FlagSetter", StringComparison.OrdinalIgnoreCase)
                              || type == "everest/flagTrigger";
            bool sets = setterType && !IsConditionAttr(h.Field) && !type.StartsWith("FlagToggle", StringComparison.OrdinalIgnoreCase);
            string raw = h.El?.Str(h.Field) ?? h.Room.El.Str(h.Field);
            bool inv = MapSearch.SplitFlags(raw).Any(f => f.Flag == h.Value && f.Inverted);
            list.Add(new FlagUse(h.Value, h, sets, inv));
        }
        return list;
    }
}
