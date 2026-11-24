using System.Text.Json.Nodes;

namespace CELMapViewer;

/// <summary>
/// Room-by-room level shapes for PCG (after the classic "level design types" chart): linear, alternate path, branched
/// end/start, folded, accordion, origami and pure organic. A shape is laid out on a grid of whole screens (one cell =
/// one room of the chosen size; origami/organic also use 2×1 and 1×2 rooms) and handed to seleCte as a skeleton in
/// place of its random tree, so tiles, exits, spawn, heart and the playability checks all work as before.
/// "random" keeps seleCte's own tree (branch probability).
/// </summary>
public static class PcgLayouts
{
    public static readonly (string Id, string Label, string Hint)[] All =
    {
        ("random", "Random tree", "seleCte's own skeleton: each room attaches to the last one, or with the branch probability to any room."),
        ("linear", "Linear", "One room after another, left to right."),
        ("shortcut", "Alternate path (shortcut)", "A main run plus a second set of rooms that leaves it and rejoins it further on."),
        ("branched_end", "Branched end", "A shared opening that splits into two or three final branches; the middle one holds the heart."),
        ("branched_start", "Branched start", "Two or three opening branches that merge into one run; the spawn is in the middle one."),
        ("folded", "Folded", "Out along one row, then back along the row below: the end sits near the spawn."),
        ("accordion", "Accordion (folded)", "A snake of rows going back and forth, top to bottom."),
        ("origami", "Origami (folded)", "Rooms of mixed sizes (tall and wide) folding in every direction."),
        ("organic", "Pure organic", "A web of mixed-size rooms with branches, loops and dead ends; the end is the room farthest from the spawn."),
    };

    public static bool IsKnown(string? id) => All.Any(l => l.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static string Normalize(string? id) =>
        All.FirstOrDefault(l => l.Id.Equals(id?.Trim().Replace(' ', '_').Replace('-', '_'), StringComparison.OrdinalIgnoreCase)).Id ?? "random";

    /// <summary>A room in screen units (x right, y down).</summary>
    public readonly record struct Cell(int X, int Y, int W = 1, int H = 1)
    {
        public bool Overlaps(Cell o) => X < o.X + o.W && o.X < X + W && Y < o.Y + o.H && o.Y < Y + H;
    }

    public sealed class Plan
    {
        public List<Cell> Rooms = new();
        public HashSet<(int A, int B)> Links = new();
        public int Start, End;

        public int Add(Cell c) { Rooms.Add(c); return Rooms.Count - 1; }
        public void Link(int a, int b)
        {
            if (a == b) return;
            if (Side(Rooms[a], Rooms[b]) == null) throw new InvalidOperationException($"layout: rooms {a} and {b} are not adjacent");
            Links.Add(a < b ? (a, b) : (b, a));
        }
        public bool Linked(int a, int b) => Links.Contains(a < b ? (a, b) : (b, a));
    }

    /// <summary>Which side of <paramref name="a"/> touches <paramref name="b"/> with at least one cell of shared edge, or null.</summary>
    public static string? Side(Cell a, Cell b)
    {
        bool rowOverlap = a.Y < b.Y + b.H && b.Y < a.Y + a.H, colOverlap = a.X < b.X + b.W && b.X < a.X + a.W;
        if (rowOverlap && a.X + a.W == b.X) return "right";
        if (rowOverlap && b.X + b.W == a.X) return "left";
        if (colOverlap && a.Y + a.H == b.Y) return "down";
        if (colOverlap && b.Y + b.H == a.Y) return "up";
        return null;
    }

    public static Plan Build(string layout, int n, double proba, Random rng)
    {
        n = Math.Max(1, n);
        return Normalize(layout) switch
        {
            "linear" => Linear(n),
            "shortcut" when n >= 5 => Shortcut(n, rng),
            "branched_end" when n >= 4 => Branched(n, rng, mirror: false),
            "branched_start" when n >= 4 => Branched(n, rng, mirror: true),
            "folded" when n >= 3 => Folded(n, rng),
            "accordion" when n >= 4 => Accordion(n),
            "origami" when n >= 2 => Walk(n, rng, organic: false, proba),
            "organic" when n >= 2 => Walk(n, rng, organic: true, proba),
            _ => Linear(n),
        };
    }

    private static Plan Linear(int n)
    {
        var p = new Plan();
        for (int i = 0; i < n; i++) { p.Add(new Cell(i, 0)); if (i > 0) p.Link(i - 1, i); }
        p.End = n - 1;
        return p;
    }

    private static Plan Shortcut(int n, Random rng)
    {
        var p = new Plan();
        int m = (n + 3) / 2, a = n - m, span = m - 2, row = rng.Next(2) == 0 ? 1 : -1;
        var main = Enumerable.Range(0, m).Select(i => p.Add(new Cell(i, 0))).ToArray();
        for (int i = 1; i < m; i++) p.Link(main[i - 1], main[i]);
        var alt = new List<int>();
        int x = 1;
        for (int i = 0; i < a; i++)
        {
            int w = span / a + (i < span % a ? 1 : 0);
            alt.Add(p.Add(new Cell(x, row, w, 1)));
            if (i > 0) p.Link(alt[i - 1], alt[i]);
            x += w;
        }
        p.Link(main[1], alt[0]);
        p.Link(alt[^1], main[m - 2]);
        p.End = main[^1];
        return p;
    }

    private static Plan Branched(int n, Random rng, bool mirror)
    {
        var p = new Plan();
        int b = n >= 8 ? 3 : 2;
        int trunk = Math.Max(2, n / 3);
        if (n - trunk < b) trunk = n - b;
        int rest = n - trunk;
        // middle branch (straight on) gets the remainder; for two branches the side one goes up or down at random
        var rows = b == 3 ? new[] { -1, 1, 0 } : new[] { rng.Next(2) == 0 ? -1 : 1, 0 };
        var lens = rows.Select(_ => rest / b).ToArray();
        lens[^1] += rest % b;

        var t = Enumerable.Range(0, trunk).Select(i => p.Add(new Cell(i, 0))).ToArray();
        for (int i = 1; i < trunk; i++) p.Link(t[i - 1], t[i]);
        int mainLast = -1;
        for (int k = 0; k < rows.Length; k++)
        {
            int prev = t[^1];
            for (int i = 0; i < lens[k]; i++)
            {
                // a side branch starts beside the junction (fork), the middle one straight on
                int x = rows[k] == 0 ? trunk + i : trunk - 1 + i;
                int id = p.Add(new Cell(x, rows[k]));
                p.Link(prev, id);
                prev = id;
            }
            if (rows[k] == 0) mainLast = prev;
        }
        p.Start = t[0];
        p.End = mainLast;
        if (mirror)
        {
            for (int i = 0; i < p.Rooms.Count; i++) { var c = p.Rooms[i]; p.Rooms[i] = c with { X = -c.X - c.W }; }
            (p.Start, p.End) = (mainLast, t[0]);
        }
        return p;
    }

    private static Plan Folded(int n, Random rng)
    {
        var p = new Plan();
        int top = (n + 1) / 2, row = rng.Next(2) == 0 ? 1 : -1;
        int prev = -1;
        for (int i = 0; i < top; i++) { int id = p.Add(new Cell(i, 0)); if (prev >= 0) p.Link(prev, id); prev = id; }
        for (int i = 0; i < n - top; i++) { int id = p.Add(new Cell(top - 1 - i, row)); p.Link(prev, id); prev = id; }
        p.End = prev;
        return p;
    }

    private static Plan Accordion(int n)
    {
        var p = new Plan();
        int len = Math.Max(2, (int)Math.Ceiling(n / 3.0));
        int prev = -1;
        for (int i = 0; i < n; i++)
        {
            int r = i / len, k = i % len;
            int id = p.Add(new Cell(r % 2 == 0 ? k : len - 1 - k, r));
            if (prev >= 0) p.Link(prev, id);
            prev = id;
        }
        p.End = prev;
        return p;
    }

    private static readonly (int W, int H)[] Shapes = { (1, 1), (1, 1), (1, 1), (2, 1), (1, 2) };

    /// <summary>
    /// Origami: a folding walk of mixed-size rooms, each attached to the previous one. Organic: rooms attach to a random
    /// earlier room as often as the branch probability (at least 35%), then some neighbouring rooms get an extra opening
    /// so the level has loops; the end is the room farthest from the spawn.
    /// </summary>
    private static Plan Walk(int n, Random rng, bool organic, double proba)
    {
        var p = new Plan();
        p.Add(new Cell(0, 0));
        double branch = organic ? Math.Max(0.35, proba) : 0;
        string lastDir = "right";
        while (p.Rooms.Count < n)
        {
            bool placed = false;
            for (int attempt = 0; attempt < 200 && !placed; attempt++)
            {
                int from = p.Rooms.Count - 1;
                if (rng.NextDouble() < branch || attempt > 60) from = rng.Next(p.Rooms.Count);
                var f = p.Rooms[from];
                var (w, h) = Shapes[rng.Next(Shapes.Length)];
                // origami keeps folding: favour turning over going straight, and rarely runs left
                string dir = Pick(rng, organic
                    ? new[] { ("right", 3), ("left", 2), ("up", 2), ("down", 2) }
                    : new[] { ("right", lastDir == "right" ? 2 : 4), ("up", lastDir is "up" ? 1 : 3), ("down", lastDir is "down" ? 1 : 3), ("left", 1) });
                Cell c = dir switch
                {
                    "right" => new Cell(f.X + f.W, f.Y - h + 1 + rng.Next(f.H + h - 1), w, h),
                    "left" => new Cell(f.X - w, f.Y - h + 1 + rng.Next(f.H + h - 1), w, h),
                    "down" => new Cell(f.X - w + 1 + rng.Next(f.W + w - 1), f.Y + f.H, w, h),
                    _ => new Cell(f.X - w + 1 + rng.Next(f.W + w - 1), f.Y - h, w, h),
                };
                if (p.Rooms.Any(r => r.Overlaps(c))) continue;
                p.Link(from, p.Add(c));
                lastDir = dir;
                placed = true;
            }
            if (!placed) break; // boxed in; keep what fits
        }
        if (organic)
        {
            for (int i = 0; i < p.Rooms.Count; i++)
                for (int j = i + 1; j < p.Rooms.Count; j++)
                    if (!p.Linked(i, j) && Side(p.Rooms[i], p.Rooms[j]) != null && rng.NextDouble() < 0.3)
                        p.Link(i, j);
            var dist = Distances(p, 0);
            p.End = Enumerable.Range(0, p.Rooms.Count).OrderByDescending(i => dist[i]).ThenByDescending(i => i).First();
        }
        else p.End = p.Rooms.Count - 1;
        return p;
    }

    private static string Pick(Random rng, (string Dir, int Weight)[] options)
    {
        int roll = rng.Next(options.Sum(o => o.Weight));
        foreach (var (d, wt) in options) { if (roll < wt) return d; roll -= wt; }
        return options[0].Dir;
    }

    private static int[] Distances(Plan p, int from)
    {
        var dist = Enumerable.Repeat(int.MaxValue, p.Rooms.Count).ToArray();
        var q = new Queue<int>();
        dist[from] = 0; q.Enqueue(from);
        while (q.Count > 0)
        {
            int cur = q.Dequeue();
            foreach (var (a, b) in p.Links)
            {
                int nb = a == cur ? b : b == cur ? a : -1;
                if (nb >= 0 && dist[nb] == int.MaxValue) { dist[nb] = dist[cur] + 1; q.Enqueue(nb); }
            }
        }
        return dist;
    }

    /// <summary>Stable across runs and machines (string.GetHashCode is not).</summary>
    public static int StableSeed(string s)
    {
        uint h = 2166136261;
        foreach (byte b in System.Text.Encoding.UTF8.GetBytes(s)) { h ^= b; h *= 16777619; }
        return unchecked((int)h);
    }

    /// <summary>
    /// The plan as seleCte's data.json skeleton: name → {origin (bottom-left, y up), size, exits {side: [[c1, c2]]}},
    /// in tiles, plus the room order (start first, end last, which is how seleCte's setStartEnd picks them).
    /// </summary>
    public static (JsonObject Data, JsonArray Order) ToSkeleton(Plan p, int roomW, int roomH, Random rng)
    {
        var order = new List<int> { p.Start };
        order.AddRange(Enumerable.Range(0, p.Rooms.Count).Where(i => i != p.Start && i != p.End));
        if (p.End != p.Start) order.Add(p.End);
        var name = new Dictionary<int, string>();
        for (int k = 0; k < order.Count; k++) name[order[k]] = "room_" + (k + 1);

        var exits = Enumerable.Range(0, p.Rooms.Count).Select(_ => new Dictionary<string, JsonArray>
            { ["up"] = new(), ["down"] = new(), ["left"] = new(), ["right"] = new() }).ToArray();
        static string Opp(string s) => s switch { "up" => "down", "down" => "up", "left" => "right", _ => "left" };
        foreach (var (a, b) in p.Links.OrderBy(l => l.A).ThenBy(l => l.B))
        {
            Cell ca = p.Rooms[a], cb = p.Rooms[b];
            string side = Side(ca, cb)!;
            int lo, hi, size;
            if (side is "left" or "right")
            {
                // shared rows, in skeleton y (up): row r of cells spans y = -(r+1)*H .. -r*H
                int r0 = Math.Max(ca.Y, cb.Y), r1 = Math.Min(ca.Y + ca.H, cb.Y + cb.H);
                lo = -r1 * roomH; hi = -r0 * roomH;
                size = Math.Min(rng.Next(4, 7), Math.Max(2, hi - lo - 6));
            }
            else
            {
                int c0 = Math.Max(ca.X, cb.X), c1 = Math.Min(ca.X + ca.W, cb.X + cb.W);
                lo = c0 * roomW; hi = c1 * roomW;
                size = Math.Min(rng.Next(4, 9), Math.Max(2, hi - lo - 6));
            }
            int min = lo + 3, max = Math.Max(min, hi - size - 3);
            int c = min + rng.Next(max - min + 1);
            exits[a][side].Add(new JsonArray(c, c + size));
            exits[b][Opp(side)].Add(new JsonArray(c, c + size));
        }

        var data = new JsonObject();
        foreach (int i in order)
        {
            var cell = p.Rooms[i];
            var ex = new JsonObject();
            foreach (var (s, list) in exits[i]) ex[s] = list;
            data[name[i]] = new JsonObject
            {
                ["origin"] = new JsonArray(cell.X * roomW, -(cell.Y + cell.H) * roomH),
                ["size"] = new JsonArray(cell.W * roomW, cell.H * roomH),
                ["exits"] = ex,
            };
        }
        return (data, new JsonArray(order.Select(i => (JsonNode)name[i]).ToArray()));
    }

    /// <summary>Text sketch of a plan for logs: S = spawn, E = end, # = room.</summary>
    public static string Sketch(Plan p)
    {
        int x0 = p.Rooms.Min(r => r.X), y0 = p.Rooms.Min(r => r.Y), x1 = p.Rooms.Max(r => r.X + r.W), y1 = p.Rooms.Max(r => r.Y + r.H);
        var g = Enumerable.Range(0, y1 - y0).Select(_ => Enumerable.Repeat('.', x1 - x0).ToArray()).ToArray();
        for (int i = 0; i < p.Rooms.Count; i++)
        {
            var r = p.Rooms[i];
            char ch = i == p.Start ? 'S' : i == p.End ? 'E' : (char)('a' + i % 26);
            for (int y = r.Y; y < r.Y + r.H; y++) for (int x = r.X; x < r.X + r.W; x++) g[y - y0][x - x0] = ch;
        }
        return string.Join("\n", g.Select(row => "  " + new string(row)));
    }
}
