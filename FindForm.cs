namespace CELMapViewer;

/// <summary>
/// Edit ▸ Find (Alt+F): search the open map for entities, triggers, decals and rooms by name, attribute value,
/// flag or dialog key, and jump to a result. Modeless; Enter/double-click jumps, F3 / Shift+F3 step through results.
/// </summary>
public sealed class FindForm : Form
{
    private readonly Func<MapBin?> _map;
    private readonly Func<string?> _currentRoom;
    private readonly Action<SearchHit> _jump;

    private readonly TextBox _query = new() { Dock = DockStyle.Fill, PlaceholderText = "Type, texture, room, flag, dialog key or any attribute value…" };
    private readonly ComboBox _kind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    private readonly ComboBox _field = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    private readonly CheckBox _word = new() { Text = "Whole word", AutoSize = true };
    private readonly CheckBox _case = new() { Text = "Match case", AutoSize = true };
    private readonly CheckBox _roomOnly = new() { Text = "Current room only", AutoSize = true };
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 22, TextAlign = ContentAlignment.MiddleLeft };
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 200 };
    private List<SearchHit> _hits = new();
    private const int MaxShown = 5000;

    public FindForm(Func<MapBin?> map, Func<string?> currentRoom, Action<SearchHit> jump)
    {
        _map = map; _currentRoom = currentRoom; _jump = jump;
        Text = "Find in map";
        Size = new Size(760, 520);
        MinimumSize = new Size(520, 300);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        KeyPreview = true;
        Icon = AppInfo.LoadIcon() ?? SystemIcons.Application;

        _kind.Items.AddRange(new object[] { "All", "Entities", "Triggers", "Decals", "Rooms" });
        _kind.SelectedIndex = 0;
        _field.Items.AddRange(new object[] { "Anything", "Names", "Attribute values", "Flags", "Dialog keys" });
        _field.SelectedIndex = 0;

        _list.Columns.Add("Room", 110);
        _list.Columns.Add("Kind", 70);
        _list.Columns.Add("Name", 200);
        _list.Columns.Add("Attribute", 110);
        _list.Columns.Add("Value", 230);

        var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 64, ColumnCount = 1, RowCount = 2, Padding = new Padding(6, 6, 6, 0) };
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        top.Controls.Add(_query, 0, 0);
        var opts = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        opts.Controls.AddRange(new Control[]
        {
            new Label { Text = "In:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, _kind,
            new Label { Text = "Match:", AutoSize = true, Padding = new Padding(6, 6, 0, 0) }, _field,
            _word, _case, _roomOnly
        });
        foreach (Control c in opts.Controls) if (c is CheckBox cb) cb.Padding = new Padding(6, 4, 0, 0);
        top.Controls.Add(opts, 0, 1);

        Controls.Add(_list);
        Controls.Add(_status);
        Controls.Add(top);

        _debounce.Tick += (_, _) => { _debounce.Stop(); RunSearch(); };
        void Changed(object? s, EventArgs e) { _debounce.Stop(); _debounce.Start(); }
        _query.TextChanged += Changed;
        _kind.SelectedIndexChanged += Changed;
        _field.SelectedIndexChanged += Changed;
        _word.CheckedChanged += Changed;
        _case.CheckedChanged += Changed;
        _roomOnly.CheckedChanged += Changed;
        _list.ItemActivate += (_, _) => JumpSelected();
        _list.SelectedIndexChanged += (_, _) => { if (_list.Focused) JumpSelected(); };
        _query.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; RunSearch(); Step(1); }
            else if (e.KeyCode == Keys.Down && _list.Items.Count > 0) { e.SuppressKeyPress = true; _list.Focus(); Select(0); }
        };
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { e.Handled = true; Hide(); }
            else if (e.KeyCode == Keys.F3) { e.Handled = true; Step(e.Shift ? -1 : 1); }
        };
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        Shown += (_, _) => FocusQuery();
    }

    /// <summary>Shows the window with <paramref name="text"/> (if any) as the query, all selected.</summary>
    public void ShowFor(IWin32Window owner, string? text = null)
    {
        if (text != null) _query.Text = text;
        if (!Visible) Show(owner);
        Activate();
        FocusQuery();
        RunSearch();
    }

    /// <summary>Preset the field filter, e.g. Flags, and search.</summary>
    public void SetField(SearchField field) => _field.SelectedIndex = (int)field;

    private void FocusQuery() { _query.Focus(); _query.SelectAll(); }

    /// <summary>Re-runs the search (the map may have changed).</summary>
    public void RunSearch()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        _hits = new();
        try
        {
            var map = _map();
            if (map == null) { _status.Text = "No map is open."; return; }
            var opts = new SearchOptions((SearchKind)_kind.SelectedIndex, (SearchField)_field.SelectedIndex, _word.Checked, _case.Checked,
                                         _roomOnly.Checked ? _currentRoom() : null);
            if (_roomOnly.Checked && opts.Room == null) { _status.Text = "Select a room first (Current room only is on)."; return; }
            _hits = MapSearch.Find(map, _query.Text, opts).Take(MaxShown + 1).ToList();
            foreach (var h in _hits.Take(MaxShown))
                _list.Items.Add(new ListViewItem(new[] { h.Room.Name, h.KindLabel, h.Name, h.El == null && h.Field == "name" ? "" : h.Field, h.Value }) { Tag = h });
            string q = _query.Text.Trim();
            _status.Text = _hits.Count == 0
                ? (q.Length == 0 && opts.Field is SearchField.Anything or SearchField.Names or SearchField.Values && opts.Kind == SearchKind.All
                    ? "Type something to search, or pick Flags / Dialog keys to list them all."
                    : "No matches.")
                : $"{Math.Min(_hits.Count, MaxShown)}{(_hits.Count > MaxShown ? "+" : "")} matches in {_hits.Select(h => h.Room).Distinct().Count()} rooms. Enter / double-click jumps, F3 next, Shift+F3 previous.";
        }
        finally { _list.EndUpdate(); }
    }

    private void Select(int index)
    {
        if (_list.Items.Count == 0) return;
        index = ((index % _list.Items.Count) + _list.Items.Count) % _list.Items.Count;
        _list.SelectedIndices.Clear();
        _list.Items[index].Selected = true;
        _list.Items[index].Focused = true;
        _list.EnsureVisible(index);
    }

    /// <summary>Jumps to the next (+1) or previous (-1) result.</summary>
    public void Step(int dir)
    {
        if (_list.Items.Count == 0) return;
        int at = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] + dir : dir > 0 ? 0 : _list.Items.Count - 1;
        Select(at);
        JumpSelected();
    }

    private void JumpSelected()
    {
        if (_list.SelectedItems.Count > 0 && _list.SelectedItems[0].Tag is SearchHit h) _jump(h);
    }
}
