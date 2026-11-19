namespace CELMapViewer;

public sealed partial class MainForm
{
    private CameraForm? _cameraForm;

    private void ShowCameraReplay()
    {
        if (_cameraForm == null)
        {
            _cameraForm = new CameraForm(_canvas, () => _map);
            _canvas.Edited += () => _cameraForm.MapChanged();
        }
        _cameraForm.ShowNear(this);
    }
}
