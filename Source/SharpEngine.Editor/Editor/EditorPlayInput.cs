namespace SharpEngine.Editor;

public sealed partial class EditorRuntime
{
    private bool PlayInputRequested;
    internal bool PlayInputCaptured => _nativeApi.IsGameplayInputCaptured();

    internal void RequestPlayInputCapture()
    {
        if (PlayState != EditorPlayState.Playing || !HasLiveUi)
        {
            return;
        }
        PlayInputRequested = _nativeApi.SetGameplayInputCapture(true);
        if (!PlayInputRequested)
        {
            SetStatus("Gameplay input capture is unavailable in this native host");
        }
    }

    internal void ReleasePlayInputCapture()
    {
        PlayInputRequested = false;
        _runtimeWorld?.ClearPlayerInput();
        var Service = _inputRuntime as SharpEngine.Input.InputRuntimeService;
        Service?.SetRouteEnabled(SharpEngine.Input.InputRoute.Gameplay, false);
        _nativeApi.SetGameplayInputCapture(false);
    }

    internal void NotifyPlayInputCaptureLost()
    {
        PlayInputRequested = false;
        _runtimeWorld?.ClearPlayerInput();
        var Service = _inputRuntime as SharpEngine.Input.InputRuntimeService;
        Service?.SetRouteEnabled(SharpEngine.Input.InputRoute.Gameplay, false);
    }
}
