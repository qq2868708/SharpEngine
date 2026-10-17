using SharpEngine.Modules;

namespace SharpEngine.Editor;

public sealed partial class EditorRuntime
{
    private readonly List<IDisposable> _inputDemoRegistrations = new();
    private bool _inputDemoOpen;
    private bool _inputDemoConsume = true;
    private bool _inputDemoDirty;
    private int _inputDemoUiCount;
    private int _inputDemoGameCount;
    private float _inputDemoX = 180;
    private float _inputDemoY = 40;

    private void RegisterInputDemoCommand()
    {
        _commands.Register(new EditorCommand("core.input-demo", "Input Routing Demo",
            _ => OpenInputDemo()));
        _menus.RegisterCategory(new EditorMenuCategory("window", "input-demo", 900));
        _menus.RegisterItem(new EditorMenuItem("window", "input-demo", "core.input-demo", 0));
    }

    private void OpenInputDemo()
    {
        if (_inputRuntime is null) return;
        if (_inputDemoOpen) { _nativeApi.CreateDockWindow("input-demo", "Input Routing Demo"); return; }
        if (_nativeApi.CreateDockWindow("input-demo", "Input Routing Demo") == 0) return;
        _inputDemoOpen = true;
        _nativeApi.SetHostMarkup("input-demo", "workspace", """
            <div id="input-demo-screen" style="display: flex; flex-direction: column; padding: 24px; width: 100%; height: 100%; background-color: #101820; color: #edf3fa;">
              <div style="font-size: 24px; margin-bottom: 10px;">One input route</div>
              <div>Click the play area. WASD moves the square; Space activates.</div>
              <div style="margin-top: 6px;">Switch UI consumption, then press Space again.</div>
              <button id="demo-toggle" action="editor.input-demo-toggle" style="margin-top: 12px; padding: 8px; width: 220px;">Toggle UI consumption</button>
              <div id="demoStats" style="margin-top: 12px; height: 48px;" />
              <div style="margin-top: 10px;">Text input — gameplay stays silent while typing:</div>
              <input id="demo-text" type="text" style="margin-top: 6px; width: 100%; height: 30px;" />
              <div id="demo-play-area" data-input-surface="gameplay" tabindex="0" style="position: relative; margin-top: 18px; width: 100%; height: 160px; border: 1px #40546a; background-color: #182736;">
                <span style="position: absolute; left: 12px; top: 8px;">PLAY AREA — click to focus</span>
                <div id="demoMarker" style="pointer-events: none;" />
              </div>
            </div>
            """);
        _inputDemoRegistrations.Add(_inputRuntime.ActivateContext("engine://input/contexts/demo-ui"));
        _inputDemoRegistrations.Add(_inputRuntime.ActivateContext("engine://input/contexts/demo-gameplay"));
        _inputDemoRegistrations.Add(_inputRuntime.BindAction(InputRoute.RuntimeUi,
            "engine://input/actions/demo.confirm", 0, input =>
            {
                if (input.HostId != "input-demo" || !_inputDemoConsume) return false;
                if (input.Phase == InputActionPhase.Performed) _inputDemoUiCount++;
                _inputDemoDirty = true;
                return true;
            }));
        _inputDemoRegistrations.Add(_inputRuntime.BindAction(InputRoute.Gameplay,
            "engine://input/actions/demo.confirm", 0, input =>
            {
                if (input.HostId != "input-demo") return false;
                if (input.Phase == InputActionPhase.Performed) _inputDemoGameCount++;
                _inputDemoDirty = true;
                return true;
            }));
        _inputDemoRegistrations.Add(_inputRuntime.BindAction(InputRoute.Gameplay,
            "engine://input/actions/demo.move", 0, input =>
            {
                if (input.HostId != "input-demo") return false;
                if (input.Phase == InputActionPhase.Performed)
                {
                    _inputDemoX = Math.Clamp(_inputDemoX + input.Value.X * 3, 0, 560);
                    _inputDemoY = Math.Clamp(_inputDemoY - input.Value.Y * 3, 26, 125);
                    _inputDemoDirty = true;
                }
                return true;
            }));
        InvokeUiAction("editor.play");
        _inputDemoDirty = true;
        UpdateInputDemo();
    }

    private bool HandleInputDemoAction(string hostId, string action)
    {
        if (action == "editor.input-demo") { OpenInputDemo(); return true; }
        if (hostId != "input-demo") return false;
        if (action == "window.closed") { CloseInputDemo(); return true; }
        if (action != "editor.input-demo-toggle") return false;
        _inputDemoConsume = !_inputDemoConsume;
        _inputDemoDirty = true;
        return true;
    }

    private void UpdateInputDemo()
    {
        if (!_inputDemoOpen || !_inputDemoDirty) return;
        _inputDemoDirty = false;
        _nativeApi.SetHostMarkup("input-demo", "demoStats",
            $"<div>UI consumes Space: {(_inputDemoConsume ? "ON" : "OFF")}</div>" +
            $"<div style=\"margin-top: 6px;\">UI activations: {_inputDemoUiCount} | Gameplay activations: {_inputDemoGameCount}</div>");
        _nativeApi.SetHostMarkup("input-demo", "demoMarker",
            FormattableString.Invariant($"<div style=\"position: absolute; left: {_inputDemoX}px; top: {_inputDemoY}px; width: 28px; height: 28px; background-color: #50d9af; pointer-events: none;\" />"));
    }

    private void CloseInputDemo()
    {
        foreach (var registration in _inputDemoRegistrations.AsEnumerable().Reverse()) registration.Dispose();
        _inputDemoRegistrations.Clear();
        if (_inputDemoOpen) _nativeApi.DestroyDockWindow("input-demo");
        _inputDemoOpen = false;
    }
}
