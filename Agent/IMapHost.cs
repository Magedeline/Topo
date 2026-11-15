namespace CELMapViewer.Agent;

/// <summary>
/// The editor state AI tools work on. Implemented by the GUI (edits show up live, with undo) and by
/// <see cref="HeadlessHost"/> for <c>--mcp</c>. Map members must only be touched inside <see cref="Invoke{T}"/>.
/// </summary>
public interface IMapHost
{
    /// <summary>Runs <paramref name="f"/> where the map may be touched (the UI thread in the GUI).</summary>
    T Invoke<T>(Func<T> f);

    MapBin? Map { get; }
    string? Path { get; }
    bool Dirty { get; }
    bool ReadOnly { get; }
    Autotiler? FgTiler { get; }
    Autotiler? BgTiler { get; }
    GameLink Game { get; }

    void Open(string path);
    void Save(string path);

    /// <summary>Snapshot for undo; call before changing the map.</summary>
    void BeginEdit();

    /// <summary>After an edit: <paramref name="level"/> is the room whose content changed, or null for room add/remove/move.</summary>
    void EndEdit(BinElement? level, string summary);

    bool Undo();
    void Log(string message);

    /// <summary>What the user currently looks at in the editor window; null without a window (<c>--mcp</c>).</summary>
    EditorView? View { get; }

    /// <summary>
    /// Renders a world rectangle the way the editor canvas draws it (autotiled tiles, decals, entity sprites, triggers).
    /// Blocks until done; call it OUTSIDE <see cref="Invoke{T}"/>.
    /// </summary>
    Bitmap RenderWorld(RectangleF area, ScreenshotOptions options);

    /// <summary>Shows freshly placed items of a room as the editor's selection, so the user sees what was put where.</summary>
    void Reveal(BinElement level, IReadOnlyList<BinElement> items);
}

/// <summary>The editor window's camera, tool and selection, as the user sees them.</summary>
public sealed record EditorView(RectangleF Bounds, float Zoom, string Tool, string Layer, bool ShowEntities, bool ShowTriggers, bool ShowBg,
                                string? SelectedRoom, IReadOnlyList<(string Room, string Kind, BinElement El)> Selected, string? GameRoom, string? PalettePick);

/// <summary>Map state for the stand-alone <c>--mcp</c> server (no window).</summary>
public sealed class HeadlessHost : IMapHost
{
    private readonly object _gate = new();
    private readonly List<BinElement> _undo = new();

    public MapBin? Map { get; private set; }
    public string? Path { get; private set; }
    public bool Dirty { get; private set; }
    public bool ReadOnly { get; init; }
    public Autotiler? FgTiler { get; private set; }
    public Autotiler? BgTiler { get; private set; }
    public GameLink Game { get; } = new();

    public T Invoke<T>(Func<T> f) { lock (_gate) return f(); }

    public void Open(string path)
    {
        var map = MapBin.Load(path);
        Map = map;
        Path = System.IO.Path.GetFullPath(path);
        Dirty = false;
        _undo.Clear();
        string? mod = MapModel.ModDirOf(Path);
        FgTiler = Autotiler.For(MapModel.TilesetXml(map, true), true, mod);
        BgTiler = Autotiler.For(MapModel.TilesetXml(map, false), false, mod);
    }

    public void Save(string path)
    {
        if (Map == null) throw new InvalidOperationException("No map is open.");
        if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
        Map.Save(path);
        Path = System.IO.Path.GetFullPath(path);
        Dirty = false;
    }

    /// <summary>Uses an in-memory map (tests, scripted use); vanilla tilesets.</summary>
    public void Load(MapBin map, string? path = null)
    {
        Map = map;
        Path = path;
        Dirty = false;
        _undo.Clear();
        FgTiler = Autotiler.For(MapModel.TilesetXml(map, true), true, null);
        BgTiler = Autotiler.For(MapModel.TilesetXml(map, false), false, null);
    }

    public void BeginEdit()
    {
        if (Map == null) return;
        _undo.Add(Map.Root.Clone());
        if (_undo.Count > 50) _undo.RemoveAt(0);
    }

    public void EndEdit(BinElement? level, string summary) => Dirty = true;

    public bool Undo()
    {
        if (Map == null || _undo.Count == 0) return false;
        Map.Root = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Dirty = true;
        return true;
    }

    public void Log(string message) => Console.Error.WriteLine("[Topo] " + message);

    public EditorView? View => null;

    public void Reveal(BinElement level, IReadOnlyList<BinElement> items) { }

    private MapCanvas? _canvas;

    public Bitmap RenderWorld(RectangleF area, ScreenshotOptions options)
    {
        // The lock is held for the whole render so no edit lands while the canvas reads the rooms on a pool thread.
        lock (_gate)
        {
            if (Map == null) throw new ToolException("No map is open.");
            WindowsFormsSynchronizationContext.AutoInstall = false; // no message loop here to resume awaits on
            var canvas = _canvas ??= new MapCanvas();
            if (canvas.FgTiler != FgTiler) canvas.FgTiler = FgTiler;
            if (canvas.BgTiler != BgTiler) canvas.BgTiler = BgTiler;
            var rooms = MapModel.Levels(Map).Children.Select(l => new Room(l)).Where(r => area.IntersectsWith(r.Bounds)).ToList();
            return Task.Run(() => canvas.RenderScreenshotAsync(area, rooms, options)).GetAwaiter().GetResult();
        }
    }
}
