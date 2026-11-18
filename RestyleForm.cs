namespace CELMapViewer;

/// <summary>
/// PCG ▸ Restyle rooms: picks existing rooms and reworks their tiles with <see cref="PcgRestyle"/> (surface detail from a
/// seleCte model, tileset swap, background). Fillers and unpicked rooms are never touched. On OK, <see cref="Plan"/> holds
/// the result for the caller to apply as one undo step.
/// </summary>
public sealed class RestyleForm : Form
{
    public List<RestyleRoom>? Plan { get; private set; }

    private readonly PcgGenerator _gen;
    private readonly IReadOnlyList<Room> _rooms;
    private readonly TextBox _filter = new() { Width = 180, PlaceholderText = "filter, e.g. c- or d-0" };
    private readonly CheckedListBox _list = new() { CheckOnClick = true, MultiColumn = true, ColumnWidth = 90, Height = 150, Width = 760, BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false };
    private readonly CheckBox _surfaces = new() { Text = "Rework surfaces with the model", AutoSize = true, Checked = true };
    private readonly ComboBox _model = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly NumericUpDown _strength = new() { Minimum = 0.05m, Maximum = 1, DecimalPlaces = 2, Increment = 0.05m, Value = 0.6m, Width = 70 };
    private readonly NumericUpDown _depth = Num(1, 4, 2);
    private readonly NumericUpDown _margin = Num(0, 6, 2);
    private readonly NumericUpDown _tries = Num(1, 30, 6);
    private readonly TextBox _seed = new() { Width = 160 };
    private readonly ComboBox _tileset = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly ComboBox _from = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly ComboBox _bg = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly CheckBox _keep = new() { Text = "Keep routes: retry / skip a room when an exit or item gets harder (movement model)", AutoSize = true, Checked = true };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f) };
    private readonly Button _go = new() { Text = "Restyle", Width = 90 };
    private readonly Button _apply = new() { Text = "Apply to map", Width = 110, Enabled = false };
#if TOPO_AI
    private readonly Button _ai = new() { Text = "Hand to AI agent…", AutoSize = true };
#endif
    private readonly Label _status = new() { AutoSize = true };
    private CancellationTokenSource? _cts;
    private bool _busy;

    private static NumericUpDown Num(int min, int max, int v) => new() { Minimum = min, Maximum = max, Value = v, Width = 70 };

#if TOPO_AI
    /// <param name="askAi">AI edition only: hands the checked rooms and these settings to the agent, which restyles, compares, retunes and
    /// hand-finishes them. Null (standard edition) hides the button.</param>
    public RestyleForm(PcgGenerator gen, IReadOnlyList<Room> rooms, Room? selected, IEnumerable<(char Id, string Name)> tilesets,
                       Action<List<Room>, RestyleOptions>? askAi = null)
#else
    public RestyleForm(PcgGenerator gen, IReadOnlyList<Room> rooms, Room? selected, IEnumerable<(char Id, string Name)> tilesets)
#endif
    {
        _gen = gen;
        _rooms = rooms.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Text = "Restyle rooms — PCG (rework existing tiles, fillers and other rooms untouched)";
        Size = new Size(820, 760);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 8, 8, 0) };
        var all = new Button { Text = "Check shown", AutoSize = true };
        var none = new Button { Text = "Uncheck all", AutoSize = true };
        var sel = new Button { Text = "Selected room", AutoSize = true, Enabled = selected != null };
        top.Controls.AddRange(new Control[] { new Label { Text = "Rooms", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, _filter, all, none, sel });

        var listPanel = new Panel { Dock = DockStyle.Top, Height = 160, Padding = new Padding(8, 4, 8, 4) };
        _list.Dock = DockStyle.Fill;
        listPanel.Controls.Add(_list);

        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, Padding = new Padding(8) };
        void Row(string a, Control ca, string b, Control cb)
        {
            grid.Controls.Add(new Label { Text = a, AutoSize = true, Anchor = AnchorStyles.Left });
            grid.Controls.Add(ca);
            grid.Controls.Add(new Label { Text = b, AutoSize = true, Anchor = AnchorStyles.Left });
            grid.Controls.Add(cb);
        }
        grid.Controls.Add(new Label { Text = "", AutoSize = true });
        grid.Controls.Add(_surfaces);
        grid.SetColumnSpan(_surfaces, 3);
        Row("Model", _model, "Strength (0-1)", _strength);
        Row("Surface depth (tiles)", _depth, "Keep-clear margin (tiles)", _margin);
        Row("Seed (blank = random)", _seed, "Tries per room", _tries);
        Row("Change tileset to", _tileset, "…only tiles of", _from);
        Row("Background", _bg, "", new Label());
        grid.Controls.Add(new Label { Text = "", AutoSize = true });
        grid.Controls.Add(_keep);
        grid.SetColumnSpan(_keep, 3);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 0, 8, 4) };
#if TOPO_AI
        buttons.Controls.AddRange(askAi != null ? new Control[] { _go, _apply, _ai, _status } : new Control[] { _go, _apply, _status });
#else
        buttons.Controls.AddRange(new Control[] { _go, _apply, _status });
#endif

        Controls.Add(_log);
        Controls.Add(buttons);
        Controls.Add(grid);
        Controls.Add(listPanel);
        Controls.Add(top);

        foreach (string m in PcgGenerator.ModelNames()) _model.Items.Add(m);
        _model.SelectedIndex = 0;
        _tileset.Items.Add("keep");
        _from.Items.Add("any solid");
        foreach (var (id, name) in tilesets)
        {
            _tileset.Items.Add($"{id}  {name}");
            _from.Items.Add($"{id}  {name}");
        }
        _tileset.SelectedIndex = 0;
        _from.SelectedIndex = 0;
        _bg.Items.Add("keep");
        _bg.Items.Add("clear");
        foreach (string b in PcgGenerator.BackgroundNames().Where(b => b != "none")) _bg.Items.Add(b);
        _bg.SelectedIndex = 0;

        var check = new HashSet<Room>();
        if (selected != null) check.Add(selected);
        void Fill()
        {
            string f = _filter.Text.Trim();
            _list.Items.Clear();
            foreach (var r in _rooms.Where(r => f.Length == 0 || r.Name.Contains(f, StringComparison.OrdinalIgnoreCase)))
                _list.Items.Add(new Item(r), check.Contains(r));
        }
        _list.ItemCheck += (_, e) =>
        {
            var r = ((Item)_list.Items[e.Index]).Room;
            if (e.NewValue == CheckState.Checked) check.Add(r); else check.Remove(r);
            _status.Text = $"{check.Count} room(s) checked";
        };
        _filter.TextChanged += (_, _) => Fill();
        all.Click += (_, _) => { for (int i = 0; i < _list.Items.Count; i++) _list.SetItemChecked(i, true); };
        none.Click += (_, _) => { check.Clear(); Fill(); _status.Text = "0 room(s) checked"; };
        sel.Click += (_, _) => { if (selected != null) { check.Add(selected); Fill(); } };
        Fill();
        _status.Text = $"{check.Count} room(s) checked";

        _surfaces.CheckedChanged += (_, _) => _model.Enabled = _strength.Enabled = _depth.Enabled = _margin.Enabled = _tries.Enabled = _keep.Enabled = _surfaces.Checked;
        _go.Click += async (_, _) => await RunAsync(check.ToList());
#if TOPO_AI
        _ai.Click += (_, _) =>
        {
            if (check.Count == 0) { Log("Check at least one room."); return; }
            askAi!(check.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList(), Options());
            DialogResult = DialogResult.Cancel;
            Close();
        };
#endif
        _apply.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        FormClosing += (_, e) => { if (_busy) { _cts?.Cancel(); _gen.Cancel(); e.Cancel = true; } };

        Log("Only the checked rooms change. Their size, position, border, openings, entities and spawn stay as they are;");
        Log("surface tiles near walls/floors/ceilings are resampled with the model, new solids continue the tileset next to them.");
        Log("Fillers (the grey hatched blocks) and every other room are never touched. Apply is one undo step (Ctrl+Z).");
#if TOPO_AI
        if (askAi != null)
            Log("Hand to AI agent: the agent restyles the checked rooms with these settings, compares before/after, retunes rooms that look off, " +
                "finishes them by hand and re-verifies every exit. You review its prompt before sending it.");
#endif
    }

    private sealed record Item(Room Room) { public override string ToString() => Room.Name; }

    private void Log(string s)
    {
        if (InvokeRequired) { BeginInvoke(() => Log(s)); return; }
        _log.AppendText(s + Environment.NewLine);
    }

    private RestyleOptions Options() => new()
    {
        Model = (string)_model.SelectedItem!,
        Seed = _seed.Text.Trim(),
        Surfaces = _surfaces.Checked,
        Strength = (double)_strength.Value,
        Depth = (int)_depth.Value,
        Margin = (int)_margin.Value,
        Tries = (int)_tries.Value,
        Tileset = _tileset.SelectedIndex > 0 ? ((string)_tileset.SelectedItem!)[0] : null,
        FromTileset = _from.SelectedIndex > 0 ? ((string)_from.SelectedItem!)[0] : null,
        Background = (string)_bg.SelectedItem!,
        KeepRoutes = _keep.Checked,
    };

    private async Task RunAsync(List<Room> rooms)
    {
        if (rooms.Count == 0) { Log("Check at least one room."); return; }
        var o = Options();
        if (!o.Surfaces && o.Tileset == null && o.Background == "keep") { Log("Nothing to do: enable surfaces, pick a tileset or a background."); return; }
        _busy = true;
        _go.Enabled = _apply.Enabled = false;
        _status.Text = $"Restyling {rooms.Count} room(s)…";
        _cts = new CancellationTokenSource();
        try
        {
            var ordered = rooms.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
            Plan = await Task.Run(() => PcgRestyle.Plan(_gen, ordered, o, Log, _cts.Token));
            Log($"--- seed \"{o.Seed}\", model {o.Model}, strength {o.Strength:0.00}, depth {o.Depth} ---");
            foreach (var r in Plan) Log("  " + PcgRestyle.Describe(r));
            int changed = Plan.Count(r => r.Fg != null || r.Bg != null);
            _status.Text = $"{changed}/{Plan.Count} room(s) changed — Apply, or Restyle again for another variant";
            _apply.Enabled = changed > 0;
        }
        catch (Exception ex)
        {
            string msg = ex is MoonSharp.Interpreter.InterpreterException ie ? ie.DecoratedMessage ?? ie.Message : ex.Message;
            Log(ex is OperationCanceledException || msg.Contains("cancelled") ? "Cancelled." : "Failed: " + msg);
            _status.Text = "";
        }
        finally
        {
            _busy = false;
            _go.Enabled = true;
        }
    }
}
