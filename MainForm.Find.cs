namespace CELMapViewer;

public sealed partial class MainForm
{
    private FindForm? _find;

    // Alt+F is also the &File mnemonic; catch it first so it opens Find (as in Lönn Searcher). Alt, then F still opens File.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Alt | Keys.F)) { ShowFind(null); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private FindForm FindWindow => _find ??= new FindForm(() => _map, () => SelectedRoom()?.Name, JumpTo);

    private void ShowFind(SearchField? field)
    {
        if (_map == null) { Log("Open a map first."); return; }
        if (field is { } f) FindWindow.SetField(f);
        // pre-fill with the selected item's type the first time, like most editors do with the word under the cursor
        string? seed = _find == null && _canvas.Selected?.El is { } el && !_canvas.Selected.Decal ? el.Name : null;
        FindWindow.ShowFor(this, seed);
    }

    private void FindStep(int dir)
    {
        if (_find == null) { ShowFind(null); return; }
        _find.Step(dir);
    }

    /// <summary>Selects a search hit in the editor and centres the view on it.</summary>
    private void JumpTo(SearchHit h)
    {
        var room = _canvas.Rooms.FirstOrDefault(r => r.El == h.Room.El);
        if (room == null) { Log($"Room {h.Room.Name} is no longer in the map; search again."); return; }
        var sel = h.El == null ? Selection.OfRoom(room) : new Selection(room, h.El, h.Trigger, h.Decal);
        // the item may have been deleted since the search
        bool still = h.El == null || room.El.Children.Any(c => c.Children.Contains(h.El));
        if (!still) { Log($"That {h.KindLabel.ToLowerInvariant()} was removed; search again."); _find?.RunSearch(); return; }
        SelectRoomInList(room);
        _canvas.Select(sel);
        if (h.El == null) _canvas.FocusRoom(room);
        else
        {
            var box = MapCanvas.BoxOf(sel);
            if (_canvas.Zoom < 1f) _canvas.SetZoom(1f);
            _canvas.CenterOn(new PointF(box.X + box.Width / 2, box.Y + box.Height / 2));
        }
        _status.Text = $"{h.KindLabel} {h.Name} in {room.Name}" + (h.Field is "name" or "texture" ? "" : $"  ({h.Field} = {h.Value})");
    }
}
