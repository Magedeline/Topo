using System.Drawing.Drawing2D;

namespace CELMapViewer;

/// <summary>
/// View ▸ Camera replay (Ctrl+Shift+C): plays back a camera recording made in game with the Lönn Camera Simulator mod over the
/// map, next to Topo's own simulated camera for the map as it is now. Edit a camera trigger, flag or killbox and the
/// simulated camera updates at once; force flags on/off to try flag-controlled cameras.
/// </summary>
public sealed class CameraForm : Form
{
    public const string RecorderUrl = "https://gamebanana.com/tools/25020";

    private readonly MapCanvas _canvas;
    private readonly Func<MapBin?> _map;

    private readonly ComboBox _sessions = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly Button _latest = new() { Text = "Latest", AutoSize = true };
    private readonly Button _folder = new() { Text = "Folder", AutoSize = true };
    private readonly Button _first = new() { Text = "|<", Width = 36 }, _prev = new() { Text = "<", Width = 36 },
                            _play = new() { Text = "Play", Width = 56 }, _next = new() { Text = ">", Width = 36 }, _last = new() { Text = ">|", Width = 36 };
    private readonly ComboBox _speed = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 64 };
    private readonly TrackBar _slider = new() { Dock = DockStyle.Fill, TickStyle = TickStyle.None, Minimum = 0, Maximum = 0 };
    private readonly Label _info = new() { Dock = DockStyle.Fill, AutoSize = false };
    private readonly ListBox _flags = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly Button _clearFlags = new() { Text = "Clear overrides", Dock = DockStyle.Bottom, Height = 26 };
    private readonly LinkLabel _help = new() { Dock = DockStyle.Top, AutoSize = false, Height = 54, Visible = false };
    private readonly CheckBox _trail = Layer("Player trail", true), _box = Layer("Player hitbox", true), _rec = Layer("Recorded camera", true),
                              _sim = Layer("Simulated camera", true), _target = Layer("Camera target", false), _trig = Layer("Active triggers", true),
                              _follow = Layer("Follow camera", true);
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 16 };
    private readonly System.Windows.Forms.Timer _watch = new() { Interval = 500 };

    private List<CameraRun> _runs = new();
    private CameraRun? _run;
    private CameraSim.Result? _result;
    private readonly Dictionary<string, bool> _overrides = new();
    private int _frame;
    private double _clock;
    private bool _useLatest = true, _filling;
    private DateTime _lastTick;

    private static CheckBox Layer(string text, bool on) => new() { Text = text, Checked = on, AutoSize = true };

    public CameraForm(MapCanvas canvas, Func<MapBin?> map)
    {
        _canvas = canvas; _map = map;
        Text = "Camera replay";
        Size = new Size(460, 600);
        MinimumSize = new Size(380, 420);
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        KeyPreview = true;
        Icon = AppInfo.LoadIcon() ?? SystemIcons.Application;

        _speed.Items.AddRange(new object[] { "0.25x", "0.5x", "1x", "1.5x", "2x" });
        _speed.SelectedIndex = 2;

        var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 32, ColumnCount = 3, Padding = new Padding(4) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.Controls.Add(_sessions, 0, 0);
        top.Controls.Add(_latest, 1, 0);
        top.Controls.Add(_folder, 2, 0);

        var transport = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, WrapContents = false, Padding = new Padding(2) };
        transport.Controls.AddRange(new Control[] { _first, _prev, _play, _next, _last, _speed });
        var sliderRow = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(4, 0, 4, 0) };
        sliderRow.Controls.Add(_slider);

        var infoBox = new GroupBox { Text = "Frame", Dock = DockStyle.Top, Height = 132 };
        infoBox.Controls.Add(_info);

        var layers = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = true };
        layers.Controls.AddRange(new Control[] { _trail, _box, _rec, _sim, _target, _trig, _follow });
        var layerBox = new GroupBox { Text = "Draw", Dock = DockStyle.Left, Width = 170 };
        layerBox.Controls.Add(layers);
        var flagBox = new GroupBox { Text = "Flags (click: auto → on → off)", Dock = DockStyle.Fill };
        flagBox.Controls.Add(_flags);
        flagBox.Controls.Add(_clearFlags);
        var bottom = new Panel { Dock = DockStyle.Fill };
        bottom.Controls.Add(flagBox);
        bottom.Controls.Add(layerBox);

        Controls.Add(bottom);
        Controls.Add(infoBox);
        Controls.Add(sliderRow);
        Controls.Add(transport);
        Controls.Add(_help);
        Controls.Add(top);

        _help.Text = "No camera recordings yet. Topo replays recordings made in game by the Lönn Camera Simulator mod "
                     + "(by rifs): install it, then press F8 in a level to record.";
        _help.LinkArea = new LinkArea(_help.Text.IndexOf("Lönn Camera Simulator", StringComparison.Ordinal), "Lönn Camera Simulator".Length);
        _help.LinkClicked += (_, _) => Open(RecorderUrl);

        _sessions.SelectedIndexChanged += (_, _) =>
        {
            if (_filling || _sessions.SelectedIndex < 0 || _sessions.SelectedIndex >= _runs.Count) return;
            _useLatest = false;
            LoadRun(_runs[_sessions.SelectedIndex].Path, jump: true);
        };
        _latest.Click += (_, _) => { _useLatest = true; RefreshSessions(); };
        _folder.Click += (_, _) =>
        {
            string? dir = CameraRun.RunsDir();
            if (dir != null && Directory.Exists(dir)) Open(dir);
            else Open(RecorderUrl);
        };
        _first.Click += (_, _) => Seek(0);
        _last.Click += (_, _) => Seek(int.MaxValue);
        _prev.Click += (_, _) => Seek(_frame - 1);
        _next.Click += (_, _) => Seek(_frame + 1);
        _play.Click += (_, _) => TogglePlay();
        _slider.Scroll += (_, _) => Seek(_slider.Value);
        foreach (var cb in new[] { _trail, _box, _rec, _sim, _target, _trig, _follow }) cb.CheckedChanged += (_, _) => _canvas.Invalidate();
        _flags.MouseClick += (_, e) => CycleFlag(_flags.IndexFromPoint(e.Location));
        _clearFlags.Click += (_, _) => { _overrides.Clear(); Resimulate(); };
        _tick.Tick += (_, _) => Advance();
        _watch.Tick += (_, _) => WatchLatest();

        KeyDown += (_, e) =>
        {
            if (ActiveControl is ComboBox) return;
            switch (e.KeyCode)
            {
                case Keys.Space: TogglePlay(); break;
                case Keys.Left: Seek(_frame - 1); break;
                case Keys.Right: Seek(_frame + 1); break;
                case Keys.Escape: Hide(); break;
                default: return;
            }
            e.Handled = e.SuppressKeyPress = true;
        };
        VisibleChanged += (_, _) =>
        {
            _canvas.WorldOverlay = Visible ? DrawOverlay : null;
            if (Visible) { RefreshSessions(); _watch.Start(); }
            else { _tick.Stop(); _watch.Stop(); _play.Text = "Play"; }
            _canvas.Invalidate();
        };
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }

    private static void Open(string target)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception) { /* nothing to open with */ }
    }

    public void ShowNear(Form owner)
    {
        if (!Visible)
        {
            var wa = Screen.FromControl(owner).WorkingArea;
            Location = new Point(Math.Min(owner.Right - Width - 24, wa.Right - Width), Math.Max(wa.Top, owner.Top + 90));
            Show(owner);
        }
        Activate();
    }

    /// <summary>The map was edited: re-run the simulation so the camera follows the new triggers.</summary>
    public void MapChanged() { if (Visible) Resimulate(); }

    private void RefreshSessions()
    {
        _runs = CameraRun.List();
        _filling = true;
        _sessions.BeginUpdate();
        _sessions.Items.Clear();
        foreach (var r in _runs) _sessions.Items.Add(r.Describe());
        _sessions.EndUpdate();
        _filling = false;
        _help.Visible = _runs.Count == 0;
        if (_runs.Count == 0)
        {
            _run = null; _result = null;
            _help.Text = (CameraRun.RecorderInstalled() ? "No camera recordings yet: press F8 in a level to record (Topo replays recordings of the "
                                                         : "No camera recordings yet. Topo replays recordings made in game by the ")
                         + "Lönn Camera Simulator mod (by rifs)" + (CameraRun.RecorderInstalled() ? ")." : ": install it, then press F8 in a level to record.");
            _help.LinkArea = new LinkArea(_help.Text.IndexOf("Lönn Camera Simulator", StringComparison.Ordinal), "Lönn Camera Simulator".Length);
            UpdateFlags();
            UpdateUi();
            return;
        }
        string? want = _useLatest ? CameraRun.LatestName() ?? _runs[0].FileName : _run?.FileName;
        int idx = Math.Max(0, _runs.FindIndex(r => r.FileName == want));
        _filling = true;
        _sessions.SelectedIndex = idx;
        _filling = false;
        if (_run?.Path != _runs[idx].Path || _run.Size != _runs[idx].Size) LoadRun(_runs[idx].Path, jump: _run?.Path != _runs[idx].Path);
    }

    private void WatchLatest()
    {
        if (!_useLatest) return;
        string? latest = CameraRun.LatestName();
        string? dir = CameraRun.RunsDir();
        if (latest == null || dir == null) return;
        string path = Path.Combine(dir, latest);
        long size;
        try { size = new FileInfo(path).Length; } catch (IOException) { return; }
        if (_run?.Path == path && _run.Size == size) return;
        bool atEnd = _run == null || _frame >= _run.Frames.Count - 1;
        if (_run?.Path != path) RefreshSessions();
        else
        {
            LoadRun(path, jump: false);
            if (atEnd && !_tick.Enabled) Seek(int.MaxValue); // live: stay on the newest frame
        }
    }

    private void LoadRun(string path, bool jump)
    {
        try { _run = CameraRun.Load(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _info.Text = "Could not read the recording: " + ex.Message;
            return;
        }
        Resimulate();
        if (jump)
        {
            _frame = 0;
            if (_run.Frames.Count > 0 && FindRoom(_run.Frames[0].Room) is { } r) _canvas.FocusRoom(r);
        }
        Seek(_frame);
    }

    private Room? FindRoom(string name)
    {
        name = CameraRun.NormalizeRoom(name);
        return _canvas.Rooms.FirstOrDefault(r => r.Name == name);
    }

    private void Resimulate()
    {
        var map = _map();
        _result = _run != null && map != null ? CameraSim.Run(_run, map, _overrides) : null;
        UpdateFlags();
        UpdateUi();
        _canvas.Invalidate();
    }

    private void TogglePlay()
    {
        if (_run == null || _run.Frames.Count == 0) return;
        if (_tick.Enabled) { _tick.Stop(); _play.Text = "Play"; return; }
        if (_frame >= _run.Frames.Count - 1) _frame = 0;
        _clock = 0;
        _lastTick = DateTime.UtcNow;
        _tick.Start();
        _play.Text = "Pause";
    }

    private void Advance()
    {
        if (_run == null) { _tick.Stop(); return; }
        var now = DateTime.UtcNow;
        double speed = _speed.SelectedIndex switch { 0 => 0.25, 1 => 0.5, 3 => 1.5, 4 => 2, _ => 1 };
        _clock += (now - _lastTick).TotalSeconds * speed;
        _lastTick = now;
        int f = _frame;
        while (f < _run.Frames.Count - 1)
        {
            float dt = _run.Frames[f].Dt > 0 ? _run.Frames[f].Dt : 1f / 60;
            if (_clock < dt) break;
            _clock -= dt;
            f++;
        }
        if (f != _frame) Seek(f);
        if (_frame >= _run.Frames.Count - 1) { _tick.Stop(); _play.Text = "Play"; }
    }

    private void Seek(int frame)
    {
        int n = _run?.Frames.Count ?? 0;
        _frame = n == 0 ? 0 : Math.Clamp(frame, 0, n - 1);
        UpdateUi();
        if (_follow.Checked && Cam() is { } c) _canvas.CenterOn(new PointF(c.X + CameraSim.W / 2, c.Y + CameraSim.H / 2));
        _canvas.Invalidate();
    }

    private PointF? Cam() =>
        _result != null && _frame < _result.Frames.Count && _sim.Checked ? _result.Frames[_frame].Camera
        : _run != null && _frame < _run.Frames.Count ? _run.Frames[_frame].Camera : null;

    private void UpdateUi()
    {
        int n = _run?.Frames.Count ?? 0;
        _slider.Maximum = Math.Max(0, n - 1);
        _slider.Value = Math.Min(_frame, _slider.Maximum);
        foreach (var b in new[] { _first, _prev, _play, _next, _last }) b.Enabled = n > 0;
        if (_run == null || n == 0) { _info.Text = _run == null ? "" : "The recording has no frames yet."; return; }

        var f = _run.Frames[_frame];
        double t = _run.Frames.Take(_frame).Sum(x => x.Dt > 0 ? x.Dt : 1 / 60.0);
        var lines = new List<string>
        {
            $"Frame {_frame + 1}/{n}   {t:0.00}s   room {CameraRun.NormalizeRoom(f.Room)}" + (FindRoom(f.Room) == null ? " (not in this map)" : ""),
            $"Player {f.X:0}, {f.Y:0}   speed {f.Vx:0}, {f.Vy:0}   state {f.State}",
        };
        if (f.Camera is { } rc) lines.Add($"Recorded camera {rc.X:0.0}, {rc.Y:0.0}");
        if (_result != null && _frame < _result.Frames.Count)
        {
            var s = _result.Frames[_frame];
            float d = _result.Divergence[_frame];
            lines.Add($"Simulated camera {s.Camera.X:0.0}, {s.Camera.Y:0.0}" + (s.UsedRecorded ? "  (uses recorded: cutscene/other map)" : "")
                      + (float.IsNaN(d) ? "" : $"   off by {d:0.0}px"));
            if (s.ActiveTriggers.Count > 0) lines.Add("Triggers: " + string.Join(", ", s.ActiveTriggers.Select(x => x.Name).Distinct()));
            lines.Add($"Largest difference in this recording: {_result.MaxDivergence:0.0}px"
                      + (_overrides.Count > 0 ? $"   ({_overrides.Count} flag override{(_overrides.Count == 1 ? "" : "s")})" : ""));
            if (_result.Unsupported.Count > 0) lines.Add("Not simulated: " + string.Join(", ", _result.Unsupported));
        }
        _info.Text = string.Join("\n", lines);
        UpdateFlagStates();
    }

    // ---- flags ----

    private List<string> _flagNames = new();

    private void UpdateFlags()
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        if (_run != null)
        {
            foreach (string rn in _run.Frames.Select(f => f.Room).Distinct())
                if (FindRoom(rn) is { } room) names.UnionWith(CameraSim.CameraFlags(room));
        }
        names.UnionWith(_overrides.Keys);
        _flagNames = names.ToList();
        UpdateFlagStates();
    }

    private void UpdateFlagStates()
    {
        var current = _result != null && _frame < _result.Flags.Count ? _result.Flags[_frame] : new HashSet<string>();
        var items = _flagNames.Select(f =>
        {
            string mode = _overrides.TryGetValue(f, out bool on) ? (on ? "forced on " : "forced off") : "auto      ";
            return $"{(current.Contains(f) ? "■" : "□")}  {mode}  {f}";
        }).ToList();
        if (items.Count == 0) items.Add("(no camera trigger in the recorded rooms reads a flag)");
        if (_flags.Items.Cast<string>().SequenceEqual(items)) return;
        int top = _flags.TopIndex;
        _flags.BeginUpdate();
        _flags.Items.Clear();
        _flags.Items.AddRange(items.Cast<object>().ToArray());
        _flags.TopIndex = Math.Min(top, Math.Max(0, _flags.Items.Count - 1));
        _flags.EndUpdate();
    }

    private void CycleFlag(int index)
    {
        if (index < 0 || index >= _flagNames.Count) return;
        string f = _flagNames[index];
        if (!_overrides.TryGetValue(f, out bool on)) _overrides[f] = true;
        else if (on) _overrides[f] = false;
        else _overrides.Remove(f);
        Resimulate();
    }

    // ---- drawing ----

    private void DrawOverlay(Graphics g)
    {
        if (_run == null || _run.Frames.Count == 0) return;
        float z = _canvas.Zoom;
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            var f = _run.Frames[_frame];
            string room = f.Room;
            if (_trail.Checked)
            {
                // the path through the current frame's room; earlier frames solid, later ones faint
                using var past = new Pen(Color.FromArgb(220, 255, 120, 220), 1.5f / z);
                using var future = new Pen(Color.FromArgb(90, 255, 120, 220), 1f / z);
                DrawPath(g, 0, _frame, room, past);
                DrawPath(g, _frame, _run.Frames.Count - 1, room, future);
            }
            if (_trig.Checked && _result != null && _frame < _result.Frames.Count && FindRoom(room) is { } r)
            {
                using var tb = new SolidBrush(Color.FromArgb(60, 120, 200, 255));
                using var tp = new Pen(Color.FromArgb(220, 120, 200, 255), 2 / z);
                foreach (var t in _result.Frames[_frame].ActiveTriggers)
                {
                    var box = MapModel.Box(r, t, true);
                    g.FillRectangle(tb, box);
                    g.DrawRectangle(tp, box.X, box.Y, box.Width, box.Height);
                }
            }
            if (_box.Checked)
            {
                using var hb = new Pen(Color.FromArgb(255, 255, 80, 80), 1 / z);
                g.DrawRectangle(hb, f.Hitbox.X, f.Hitbox.Y, f.Hitbox.Width, f.Hitbox.Height);
                using var v = new Pen(Color.FromArgb(200, 255, 255, 120), 1 / z);
                g.DrawLine(v, f.X, f.Y - f.Hitbox.Height / 2, f.X + f.Vx * 0.15f, f.Y - f.Hitbox.Height / 2 + f.Vy * 0.15f);
            }
            if (_rec.Checked && f.Camera is { } rc)
            {
                using var p = new Pen(Color.FromArgb(240, 255, 200, 60), 2 / z);
                g.DrawRectangle(p, rc.X, rc.Y, CameraSim.W, CameraSim.H);
            }
            if (_result != null && _frame < _result.Frames.Count)
            {
                var s = _result.Frames[_frame];
                if (_sim.Checked)
                {
                    using var p = new Pen(Color.FromArgb(240, 80, 230, 120), 2 / z) { DashStyle = DashStyle.Dash };
                    g.DrawRectangle(p, s.Camera.X, s.Camera.Y, CameraSim.W, CameraSim.H);
                }
                if (_target.Checked)
                {
                    using var p = new Pen(Color.FromArgb(200, 80, 230, 230), 1 / z) { DashStyle = DashStyle.Dot };
                    g.DrawRectangle(p, s.Target.X, s.Target.Y, CameraSim.W, CameraSim.H);
                    float cx = s.Target.X + CameraSim.W / 2, cy = s.Target.Y + CameraSim.H / 2, k = 4 / Math.Min(1, z);
                    g.DrawLine(p, cx - k, cy, cx + k, cy);
                    g.DrawLine(p, cx, cy - k, cx, cy + k);
                }
            }
        }
        finally { g.Restore(state); }
    }

    private void DrawPath(Graphics g, int from, int to, string room, Pen pen)
    {
        var pts = new List<PointF>();
        for (int i = from; i <= to && i < _run!.Frames.Count; i++)
        {
            var f = _run.Frames[i];
            if (f.Room != room) { if (pts.Count > 1) g.DrawLines(pen, pts.ToArray()); pts.Clear(); continue; }
            pts.Add(new PointF(f.X, f.Y - f.Hitbox.Height / 2));
        }
        if (pts.Count > 1) g.DrawLines(pen, pts.ToArray());
    }
}
