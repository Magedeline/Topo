using CELMapViewer.Agent;

namespace CELMapViewer;

public sealed class RestyleOptions
{
    /// <summary>Foreground model whose surface texture is sampled (built-in or custom fg model).</summary>
    public string Model = "pe_d5_full";
    public string Seed = "";
    /// <summary>Rework the foreground's surfaces. False = only tileset swap / background.</summary>
    public bool Surfaces = true;
    /// <summary>Chance 0..1 that an editable cell is resampled.</summary>
    public double Strength = 0.6;
    /// <summary>How many tiles from a solid/air surface may change (1-4).</summary>
    public int Depth = 2;
    /// <summary>Resamples per room before the room is left as it was.</summary>
    public int Tries = 6;
    /// <summary>Tiles kept untouched around openings, entities and the spawn.</summary>
    public int Margin = 2;
    /// <summary>Glyph every solid becomes (null = keep the room's tilesets).</summary>
    public char? Tileset;
    /// <summary>Only swap this glyph (null = every solid glyph).</summary>
    public char? FromTileset;
    /// <summary>"keep", "clear" or a background model name to regenerate the bg layer.</summary>
    public string Background = "keep";
    /// <summary>Reject a result if any exit or item gets harder to reach (movement model).</summary>
    public bool KeepRoutes = true;
}

public sealed class RestyleRoom
{
    public Room Room = null!;
    public TileGrid? Fg, Bg;
    public int Added, Removed, Swapped, Tries;
    public bool Applied;
    public string Note = "";
    public string Tech = "";
}

/// <summary>
/// "PCG restyle": reworks the tiles of rooms that already exist without moving them, resizing them or touching anything
/// outside them (fillers, other rooms). Only cells near a solid/air surface are resampled with a seleCte model, away from
/// the border, openings, entities and the spawn; new solids take the nearest original tileset. Every result is judged
/// with the movement model against the room as it was and retried with a new seed (or dropped) when an exit or item gets
/// harder. Optionally swaps tilesets and regenerates the background. Nothing is written: <see cref="Apply"/> does that.
/// </summary>
public static class PcgRestyle
{
    public static List<RestyleRoom> Plan(PcgGenerator gen, IReadOnlyList<Room> rooms, RestyleOptions o, Action<string> log, CancellationToken ct = default)
    {
        string seed = o.Seed.Length > 0 ? o.Seed : Random.Shared.Next(100000, 999999).ToString();
        o.Seed = seed;
        var results = rooms.Select(r => new RestyleRoom { Room = r }).ToList();

        if (o.Surfaces)
        {
            var sampler = gen.SurfaceSampler(o.Model, log);
            var state = results.Select(r => new SurfaceState(r.Room, o)).ToList();
            var pending = Enumerable.Range(0, results.Count).Where(i => state[i].EditableCount > 0).ToList();
            foreach (int i in Enumerable.Range(0, results.Count).Except(pending)) results[i].Note = "nothing editable (no surfaces away from exits/entities)";
            for (int t = 0; t < Math.Max(1, o.Tries) && pending.Count > 0; t++)
            {
                ct.ThrowIfCancellationRequested();
                var outRows = sampler(pending.Select(i => (state[i].Rows, state[i].Edit)).ToList(), $"{seed}#{t}", o.Strength);
                var still = new List<int>();
                for (int k = 0; k < pending.Count; k++)
                {
                    int i = pending[k];
                    var r = results[i];
                    r.Tries = t + 1;
                    var grid = state[i].Decode(outRows[k], out int added, out int removed);
                    if (added + removed == 0) { still.Add(i); r.Note = "no change sampled"; continue; }
                    string? why = o.KeepRoutes ? state[i].Worse(grid) : null;
                    if (why != null) { still.Add(i); r.Note = why; continue; }
                    (r.Fg, r.Added, r.Removed, r.Note) = (grid, added, removed, "");
                }
                pending = still;
            }
            foreach (int i in pending) results[i].Note = $"kept as it was after {results[i].Tries} tries ({results[i].Note})";
        }

        foreach (var r in results)
        {
            ct.ThrowIfCancellationRequested();
            if (o.Tileset is char to)
            {
                var g = r.Fg ?? r.Room.Grid(true).Clone();
                for (int y = 0; y < g.Rows; y++)
                    for (int x = 0; x < g.Cols; x++)
                        if (g[x, y] != '0' && g[x, y] != to && (o.FromTileset == null || g[x, y] == o.FromTileset)) { g[x, y] = to; r.Swapped++; }
                if (r.Swapped > 0) r.Fg = g;
            }
            if (o.Background == "clear") r.Bg = new TileGrid(r.Room.Cols, r.Room.Rows);
            else if (o.Background != "keep")
            {
                var rows = gen.GenerateBackground(o.Background, r.Room.Cols, r.Room.Rows, $"{seed}/{r.Room.Name}", log);
                r.Bg = TileGrid.Parse(string.Join("\n", rows), r.Room.Cols, r.Room.Rows);
            }
            if (r.Fg != null) r.Tech = TechSummary(WithFg(r.Room, r.Fg));
        }
        return results;
    }

    /// <summary>Writes the planned grids into the rooms (call inside an undo step). Returns how many rooms changed.</summary>
    public static int Apply(IEnumerable<RestyleRoom> plan)
    {
        int n = 0;
        foreach (var r in plan)
        {
            bool any = false;
            if (r.Fg != null) { Copy(r.Fg, r.Room.Grid(true)); r.Room.CommitGrid(true); any = true; }
            if (r.Bg != null) { Copy(r.Bg, r.Room.Grid(false)); r.Room.CommitGrid(false); any = true; }
            r.Applied = any;
            if (any) n++;
        }
        return n;
    }

    public static string Describe(RestyleRoom r)
    {
        var parts = new List<string>();
        if (r.Added + r.Removed > 0) parts.Add($"+{r.Added}/-{r.Removed} tiles (try {r.Tries})");
        if (r.Swapped > 0) parts.Add($"{r.Swapped} tiles swapped");
        if (r.Bg != null) parts.Add("bg redone");
        if (r.Tech.Length > 0) parts.Add(r.Tech);
        if (r.Note.Length > 0) parts.Add(r.Note);
        return $"{r.Room.Name}: {(parts.Count > 0 ? string.Join(", ", parts) : "unchanged")}";
    }

    private static void Copy(TileGrid from, TileGrid to)
    {
        for (int y = 0; y < to.Rows; y++)
            for (int x = 0; x < to.Cols; x++)
                to[x, y] = from[x, y];
    }

    /// <summary>A throwaway copy of the room with another fg grid (for the movement model).</summary>
    internal static Room WithFg(Room room, TileGrid fg)
    {
        var copy = new Room(room.El.Clone());
        Copy(fg, copy.Grid(true));
        return copy;
    }

    private static string TechSummary(Room room)
    {
        var (_, rep) = RoomAnalysis.Report(room, Starts(room));
        return "tech " + rep["required_tech"];
    }

    /// <summary>The spawn, or for a room without one every opening.</summary>
    private static List<(int X, int Y)>? Starts(Room room)
    {
        if (room.EntitiesEl.Children.Any(MapModel.IsSpawn)) return null;
        var b = RoomAnalysis.Build(room);
        var cells = b.Exits.SelectMany(e => e.Cells(room.Cols, room.Rows)).ToList();
        return cells.Count > 0 ? cells : null;
    }

    private static int Rank(Tech? t) => t switch { Tech.NoDash => 0, Tech.Basic => 1, Tech.Advanced => 2, _ => 3 };

    // ---- per-room surface state ----

    private sealed class SurfaceState
    {
        private const int Pad = 2; // the 3x3 MdMC window trails its target by two cells: pad so every real cell can be a target
        private readonly Room _room;
        private readonly TileGrid _orig;
        private readonly bool[,] _edit;
        private readonly char[,] _glyph; // nearest original solid glyph per cell
        private readonly List<(int X, int Y)>? _starts;
        private RoomAnalysis? _before;
        private List<Tech?>? _beforeExits;
        public readonly string[] Rows, Edit;
        public readonly int EditableCount;

        public SurfaceState(Room room, RestyleOptions o)
        {
            _room = room;
            _orig = room.Grid(true).Clone();
            int w = _orig.Cols, h = _orig.Rows;
            bool Solid(int x, int y) => _orig[Math.Clamp(x, 0, w - 1), Math.Clamp(y, 0, h - 1)] != '0';

            // distance (Chebyshev) from every cell to the nearest cell of the other kind
            var dist = new int[w, h];
            var q = new Queue<(int, int)>();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    dist[x, y] = int.MaxValue;
                    bool s = Solid(x, y);
                    bool surface = false;
                    for (int dy = -1; dy <= 1 && !surface; dy++)
                        for (int dx = -1; dx <= 1 && !surface; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if ((dx | dy) != 0 && nx >= 0 && ny >= 0 && nx < w && ny < h && Solid(nx, ny) != s) surface = true;
                        }
                    if (surface) { dist[x, y] = 1; q.Enqueue((x, y)); }
                }
            Bfs(dist, q, w, h);

            // protected: the border ring, openings, entities, the spawn's column down to its floor
            var guard = new bool[w, h];
            int m = Math.Max(0, o.Margin);
            void Protect(int x0, int y0, int x1, int y1)
            {
                for (int y = Math.Max(0, y0); y <= Math.Min(h - 1, y1); y++)
                    for (int x = Math.Max(0, x0); x <= Math.Min(w - 1, x1); x++)
                        guard[x, y] = true;
            }
            for (int x = 0; x < w; x++) { guard[x, 0] = guard[x, h - 1] = true; }
            for (int y = 0; y < h; y++) { guard[0, y] = guard[w - 1, y] = true; }
            for (int x = 0; x < w; x++)
            {
                if (!Solid(x, 0)) Protect(x - m - 1, 0, x + m + 1, m + 2);
                if (!Solid(x, h - 1)) Protect(x - m - 1, h - 3 - m, x + m + 1, h - 1);
            }
            for (int y = 0; y < h; y++)
            {
                if (!Solid(0, y)) Protect(0, y - m - 1, m + 2, y + m + 1);
                if (!Solid(w - 1, y)) Protect(w - 3 - m, y - m - 1, w - 1, y + m + 1);
            }
            foreach (var e in room.EntitiesEl.Children)
            {
                // sized entities and spikes have exact boxes; for the rest a fixed box around the origin, so the result
                // does not depend on whether sprites have loaded yet (keeps a seed reproducible)
                var b = e.Num("width") > 0 || e.Num("height") > 0 || EntityVisuals.SpikeDir(e.Name) != null
                    ? RoomAnalysis.Local(room, MapModel.Box(room, e, false))
                    : new RectangleF(e.Num("x") - 8, e.Num("y") - 16, 16, 24);
                int x0 = (int)Math.Floor(b.Left / 8f), y0 = (int)Math.Floor(b.Top / 8f);
                int x1 = (int)Math.Ceiling(b.Right / 8f) - 1, y1 = (int)Math.Ceiling(b.Bottom / 8f) - 1;
                Protect(x0 - m, y0 - m, x1 + m, y1 + m);
                foreach (var n in e.Children.Where(c => c.Name == "node"))
                {
                    int nx = (int)Math.Floor(n.Num("x") / 8f), ny = (int)Math.Floor(n.Num("y") / 8f);
                    Protect(nx - m, ny - m, nx + m, ny + m);
                }
                if (MapModel.IsSpawn(e))
                {
                    int sx = Math.Clamp((int)Math.Floor(e.Num("x") / 8f), 0, w - 1), sy = (int)Math.Floor((e.Num("y") - 1) / 8f);
                    int fy = sy;
                    while (fy < h - 1 && !Solid(sx, fy + 1)) fy++;
                    Protect(sx - m - 1, sy - m - 2, sx + m + 1, fy + 1);
                }
            }

            // thin solids (a pillar, ledge or stalactite at most 3 tiles across in either direction) are never eroded:
            // the model would happily wear them down to nothing
            bool Thin(int x, int y)
            {
                int Run(int dx, int dy)
                {
                    int n = 0, cx = x + dx, cy = y + dy;
                    while (n < 8 && Solid(cx, cy) && cx >= 0 && cy >= 0 && cx < w && cy < h) { n++; cx += dx; cy += dy; }
                    if (cx < 0 || cy < 0 || cx >= w || cy >= h) n = 8; // runs into the room edge: part of the frame
                    return n;
                }
                return 1 + Run(1, 0) + Run(-1, 0) <= 3 || 1 + Run(0, 1) + Run(0, -1) <= 3;
            }

            _edit = new bool[w, h];
            int depth = Math.Clamp(o.Depth, 1, 4);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (!guard[x, y] && dist[x, y] <= depth && !(Solid(x, y) && Thin(x, y))) { _edit[x, y] = true; EditableCount++; }

            // nearest original solid glyph, so new solids continue the tileset next to them
            _glyph = new char[w, h];
            var gd = new int[w, h];
            var gq = new Queue<(int, int)>();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    gd[x, y] = int.MaxValue;
                    if (_orig[x, y] != '0') { gd[x, y] = 0; _glyph[x, y] = _orig[x, y]; gq.Enqueue((x, y)); }
                }
            while (gq.Count > 0)
            {
                var (x, y) = gq.Dequeue();
                foreach (var (nx, ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
                    if (nx >= 0 && ny >= 0 && nx < w && ny < h && gd[nx, ny] == int.MaxValue)
                    { gd[nx, ny] = gd[x, y] + 1; _glyph[nx, ny] = _glyph[x, y]; gq.Enqueue((nx, ny)); }
            }

            // padded binary rows: two copies of the top row / left column so the window can reach every real cell
            Rows = new string[h + Pad];
            Edit = new string[h + Pad];
            for (int py = 0; py < h + Pad; py++)
            {
                int y = Math.Max(0, py - Pad);
                var rb = new char[w + Pad];
                var eb = new char[w + Pad];
                for (int px = 0; px < w + Pad; px++)
                {
                    int x = Math.Max(0, px - Pad);
                    rb[px] = Solid(x, y) ? '1' : '0';
                    eb[px] = py >= Pad && px >= Pad && _edit[x, y] ? '1' : '0';
                }
                Rows[py] = new string(rb);
                Edit[py] = new string(eb);
            }
            _starts = Starts(room);
        }

        private static void Bfs(int[,] dist, Queue<(int, int)> q, int w, int h)
        {
            while (q.Count > 0)
            {
                var (x, y) = q.Dequeue();
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h || dist[nx, ny] != int.MaxValue) continue;
                        dist[nx, ny] = dist[x, y] + 1;
                        q.Enqueue((nx, ny));
                    }
            }
        }

        /// <summary>Sampler output back to a tile grid, cleaned of specks and pockets, with tilesets restored.</summary>
        public TileGrid Decode(string[] rows, out int added, out int removed)
        {
            int w = _orig.Cols, h = _orig.Rows;
            var solid = new bool[w, h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    solid[x, y] = _edit[x, y] ? rows[y + Pad][x + Pad] == '1' : _orig[x, y] != '0';

            bool S(int x, int y) => x < 0 || y < 0 || x >= w || y >= h || solid[x, y];
            int Around(int x, int y) => (S(x + 1, y) ? 1 : 0) + (S(x - 1, y) ? 1 : 0) + (S(x, y + 1) ? 1 : 0) + (S(x, y - 1) ? 1 : 0);
            for (int pass = 0; pass < 3; pass++)
            {
                bool changed = false;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        if (!_edit[x, y]) continue;
                        int n = Around(x, y);
                        // new solid hanging by one side or none: a speck / one-tile spur
                        if (solid[x, y] && _orig[x, y] == '0' && n < 2) { solid[x, y] = false; changed = true; }
                        // new one-tile-wide spur
                        else if (solid[x, y] && _orig[x, y] == '0' && ((!S(x - 1, y) && !S(x + 1, y)) || (!S(x, y - 1) && !S(x, y + 1)))) { solid[x, y] = false; changed = true; }
                        // new hole closed on three or four sides: a pocket the autotiler draws as noise
                        else if (!solid[x, y] && _orig[x, y] != '0' && n >= 3) { solid[x, y] = true; changed = true; }
                        // new one-tile-wide slot
                        else if (!solid[x, y] && _orig[x, y] != '0' && ((S(x - 1, y) && S(x + 1, y)) || (S(x, y - 1) && S(x, y + 1)))) { solid[x, y] = true; changed = true; }
                    }
                if (!changed) break;
            }
            KeepMassesWhole(solid);
            // carved air sealed off from the room's original air is an invisible bubble: fill it back
            var airLabel = Label((x, y) => !solid[x, y], w, h, out int airCount);
            var open = new bool[airCount + 1];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (airLabel[x, y] != 0 && _orig[x, y] == '0') open[airLabel[x, y]] = true;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (airLabel[x, y] != 0 && !open[airLabel[x, y]]) solid[x, y] = true;

            var g = _orig.Clone();
            added = removed = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    bool was = _orig[x, y] != '0';
                    if (solid[x, y] == was) continue;
                    if (solid[x, y]) { g[x, y] = _glyph[x, y] == '\0' ? '1' : _glyph[x, y]; added++; }
                    else { g[x, y] = '0'; removed++; }
                }
            return g;
        }

        /// <summary>
        /// No solid mass may be cut in two (its removed cells come back) and no new solid may float on its own (removed):
        /// restyling changes the outline of a wall, never what is attached to what.
        /// </summary>
        private void KeepMassesWhole(bool[,] solid)
        {
            int w = _orig.Cols, h = _orig.Rows;
            var origLabel = Label((x, y) => _orig[x, y] != '0', w, h, out _);
            for (int pass = 0; pass < 3; pass++)
            {
                var newLabel = Label((x, y) => solid[x, y], w, h, out int count);
                var parts = new Dictionary<int, HashSet<int>>();
                var anchored = new bool[count + 1];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        if (newLabel[x, y] == 0) continue;
                        if (origLabel[x, y] != 0)
                        {
                            anchored[newLabel[x, y]] = true;
                            if (!parts.TryGetValue(origLabel[x, y], out var set)) parts[origLabel[x, y]] = set = new HashSet<int>();
                            set.Add(newLabel[x, y]);
                        }
                    }
                var split = parts.Where(p => p.Value.Count > 1).Select(p => p.Key).ToHashSet();
                // a new mass touching two original ones bridged a gap: drop what was added there
                var bridges = parts.SelectMany(p => p.Value.Select(n => (New: n, Orig: p.Key)))
                    .GroupBy(t => t.New).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
                bool changed = false;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        if (origLabel[x, y] != 0 && !solid[x, y] && split.Contains(origLabel[x, y])) { solid[x, y] = true; changed = true; }
                        else if (newLabel[x, y] != 0 && origLabel[x, y] == 0 && (!anchored[newLabel[x, y]] || bridges.Contains(newLabel[x, y]))) { solid[x, y] = false; changed = true; }
                    }
                if (!changed) break;
            }
        }

        private static int[,] Label(Func<int, int, bool> solid, int w, int h, out int count)
        {
            var label = new int[w, h];
            count = 0;
            var q = new Queue<(int, int)>();
            for (int sy = 0; sy < h; sy++)
                for (int sx = 0; sx < w; sx++)
                {
                    if (label[sx, sy] != 0 || !solid(sx, sy)) continue;
                    label[sx, sy] = ++count;
                    q.Enqueue((sx, sy));
                    while (q.Count > 0)
                    {
                        var (x, y) = q.Dequeue();
                        foreach (var (nx, ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
                            if (nx >= 0 && ny >= 0 && nx < w && ny < h && label[nx, ny] == 0 && solid(nx, ny))
                            { label[nx, ny] = count; q.Enqueue((nx, ny)); }
                    }
                }
            return label;
        }

        /// <summary>Why the new grid is worse for the player than the original, or null when nothing got harder.</summary>
        public string? Worse(TileGrid fg)
        {
            if (_before == null)
            {
                var (b, _) = RoomAnalysis.Report(_room, _starts);
                _before = b;
                _beforeExits = b.Exits.Select(e => e.MinTech).ToList();
            }
            var (after, _) = RoomAnalysis.Report(WithFg(_room, fg), _starts);
            if (after.Exits.Count != _before.Exits.Count) return "openings changed";
            for (int i = 0; i < after.Exits.Count; i++)
                if (Rank(after.Exits[i].MinTech) > Rank(_beforeExits![i]))
                    return $"exit {after.Exits[i].Label} got harder";
            int itemsBefore = _before.Items.Count(it => it.Reachable), itemsAfter = after.Items.Count(it => it.Reachable);
            if (itemsAfter < itemsBefore) return "an item became unreachable";
            if (_before.Spawn is { } sp && after.Spawn is { } sp2 && sp == sp2 && fg[sp.X, sp.Y] != '0') return "spawn buried";
            return null;
        }
    }
}
