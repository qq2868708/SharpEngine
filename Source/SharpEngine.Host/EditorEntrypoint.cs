using SharpEngine.Input;
using System.Runtime.InteropServices;
using SharpEngine.Editor;
using SharpEngine.Logging;
using SharpEngine.Modules;
using SharpEngine.Interop;
using SharpEngine.Timeline;

namespace SharpEngine.Host;

public static class HostEntrypoint
{
    private static EditorRuntime? s_runtime;
    private static EngineModuleHost? s_engineModules;
    private static ProjectModuleHost? s_modules;
    private static ProjectCodeService? s_projectCode;
    private static InputRuntimeService? s_input;
    private static readonly List<IDisposable> s_inputHandlers = new();

    [UnmanagedCallersOnly]
    public static int Initialize(
        nint WindowHandle,
        uint Width,
        uint Height,
        nint NativeApi)
    {
        var Progress = new NativeStartupProgress(NativeApi);
        try
        {
            s_runtime = new EditorRuntime(WindowHandle, Width, Height, NativeApi);
            s_runtime.Initialize(Progress.Report);
            Progress.Report(66, "EditorIntegration", "Registering editor tools and input bindings");
            EditorFirstUse.TryRegister(s_runtime.EngineRoot,
                Path.GetDirectoryName(typeof(HostEntrypoint).Assembly.Location)!);
            s_input = InputRuntimeService.Load(s_runtime.Assets ??
                throw new InvalidOperationException("The asset registry is unavailable"));
            s_runtime.AttachInputRuntime(s_input);
            RegisterEditorInput(s_runtime, s_input);
            var ModuleRoot = Path.GetDirectoryName(s_runtime.ProjectManifest ??
                Path.Combine(s_runtime.EngineRoot, "engine.json"))!;
            s_engineModules = EngineModuleHost.Prepare("Editor",
                typeof(TimelineRuntimeModule).Assembly,
                typeof(TimelineEditorModule).Assembly);
            s_engineModules.Start((Id, Type) => Type == "Editor"
                ? new EditorModuleContext(ModuleRoot, Id, s_runtime, s_input)
                : new ModuleContext(ModuleRoot, Id, Type, s_runtime.UiRuntime, s_input, s_runtime),
                (Id, Index, Count) => Progress.Report(70 + Index * 7 / Count, Id, "Starting engine module"));
            try
            {
                s_modules = ProjectModuleIntegration.Load(s_runtime, s_input, Progress.Report);
                if (s_modules is null)
                {
                    s_runtime.CompleteComponentRegistration();
                }
            }
            catch (Exception Error)
            {
                s_runtime.SetProjectCodeAvailable(false, "Project code failed to load; edit code and rebuild to recover");
                SharpLog.Error("ProjectModules", "Project code failed to load", Error);
                s_runtime.SetProjectBuildStatus("ReloadFailed", Error.ToString());
            }
            Progress.Report(95, "ProjectCode", "Initializing project build and reload services");
            s_projectCode = new ProjectCodeService(s_runtime, s_input, s_modules);
            s_modules = null; // Ownership transferred to the build/reload service.
            return 0;
        }
        catch (Exception Exception)
        {
            SharpLog.Critical("Editor", "Initialize failed", Exception);
            s_runtime?.StopRuntimeInstance();
            s_modules?.Dispose();
            s_modules = null;
            s_engineModules?.Dispose();
            s_engineModules = null;
            DisposeInputHandlers();
            s_runtime?.AttachInputRuntime(null);
            s_input = null;
            s_runtime?.Shutdown();
            s_runtime = null;
            return -1;
        }
    }

    [UnmanagedCallersOnly]
    public static void Update(float deltaSeconds)
    {
        try
        {
            if (s_runtime is { } runtime && s_input is { } input)
            {
                input.SetRouteEnabled(InputRoute.Editor, true);
                input.SetRouteEnabled(InputRoute.RuntimeUi,
                    runtime.PlayState != EditorPlayState.Stopped);
                input.SetRouteEnabled(InputRoute.Gameplay,
                    runtime.PlayState == EditorPlayState.Playing);
                input.AdvanceFrame(deltaSeconds);
            }
            s_projectCode?.Tick();
            s_runtime?.Update(deltaSeconds);
        }
        catch (Exception exception)
        {
            SharpLog.Error("Editor", "Update failed", exception);
        }
    }

    [UnmanagedCallersOnly]
    public static void Shutdown()
    {
        try
        {
            s_runtime?.StopRuntimeInstance();
            s_projectCode?.Dispose();
            s_projectCode = null;
            s_modules?.Dispose();
            s_modules = null;
            s_engineModules?.Dispose();
            s_engineModules = null;
            DisposeInputHandlers();
            s_runtime?.AttachInputRuntime(null);
            s_input = null;
            s_runtime?.Shutdown();
        }
        catch (Exception exception)
        {
            SharpLog.Error("Editor", "Shutdown failed", exception);
        }
        finally
        {
            s_modules = null;
            s_input = null;
            s_runtime = null;
            SharpLog.Shutdown();
        }
    }

    [UnmanagedCallersOnly]
    public static void PointerDown(nint windowHandle, int x, int y, uint button)
    {
        try
        {
            s_runtime?.PointerDown(windowHandle, x, y, button);
        }
        catch (Exception exception)
        {
            SharpLog.Error("EditorInput", "PointerDown failed", exception);
        }
    }

    [UnmanagedCallersOnly]
    public static void InvokeUiAction(nint actionUtf8)
    {
        try
        {
            if (Marshal.PtrToStringUTF8(actionUtf8) is { } action)
                s_runtime?.InvokeUiAction(action);
        }
        catch (Exception exception)
        {
            SharpLog.Error("EditorInput", "UI action failed", exception);
        }
    }

    [UnmanagedCallersOnly]
    public static void ViewportInput(nint windowHandle, int x, int y,
        uint inputEvent, uint buttons, int wheelDelta)
    {
        try
        {
            s_runtime?.ViewportInput(windowHandle, x, y, inputEvent, buttons, wheelDelta);
        }
        catch (Exception exception)
        {
            SharpLog.Error("EditorInput", "ViewportInput failed", exception);
        }
    }

    [UnmanagedCallersOnly]
    public static void ViewportKey(uint key, byte pressed, byte viewportFocused)
    {
        try
        {
            s_runtime?.ViewportKey(key, pressed != 0, viewportFocused != 0);
        }
        catch (Exception exception)
        {
            SharpLog.Error("EditorInput", "ViewportKey failed", exception);
        }
    }

    [UnmanagedCallersOnly]
    public static void InputEvent(nint hostIdUtf8, uint eventKind, uint code,
        int x, int y, float valueX, float valueY, uint buttons, uint modifiers,
        byte uiHandled, byte viewportFocused)
    {
        try
        {
            if (s_runtime is not { } runtime || s_input is not { } input) return;
            var hostId = Marshal.PtrToStringUTF8(hostIdUtf8) ?? "main";
            if (!Enum.IsDefined(typeof(NativeInputEventKind), eventKind)) return;
            var kind = (NativeInputEventKind)eventKind;
            input.SetRouteEnabled(InputRoute.RuntimeUi, runtime.PlayState != EditorPlayState.Stopped);
            input.SetRouteEnabled(InputRoute.Gameplay, runtime.PlayState == EditorPlayState.Playing);
            var handled = input.ProcessNative(hostId, kind, code, x, y, valueX,
                valueY, modifiers, uiHandled != 0, viewportFocused != 0);
            if (kind == NativeInputEventKind.FocusLost)
            {
                runtime.ViewportKey(0, false, false);
                return;
            }
            if (hostId != "main") return;
            var EditorViewportInputEnabled = runtime.PlayState == EditorPlayState.Stopped;
            if (viewportFocused == 0 && kind is NativeInputEventKind.KeyDown or NativeInputEventKind.PointerDown)
                runtime.ViewportKey(0, false, false);
            if (kind == NativeInputEventKind.CaptureLost)
            {
                runtime.ViewportInput(0, x, y, 2, 0x13, 0);
                return;
            }
            if (kind is NativeInputEventKind.KeyUp)
                runtime.ViewportKey(code, false, viewportFocused != 0);
            else if (EditorViewportInputEnabled && !handled && kind is NativeInputEventKind.KeyDown &&
                     viewportFocused != 0)
                runtime.ViewportKey(code, true, true);
            if (!EditorViewportInputEnabled || viewportFocused == 0) return;
            var viewportEvent = kind switch
            {
                NativeInputEventKind.PointerDown => 1u,
                NativeInputEventKind.PointerUp => 2u,
                NativeInputEventKind.Wheel => 3u,
                NativeInputEventKind.PointerMove => 0u,
                _ => uint.MaxValue,
            };
            if (viewportEvent == uint.MaxValue) return;
            if (!handled || kind == NativeInputEventKind.PointerUp)
                runtime.ViewportInput(0, x, y, viewportEvent, buttons,
                    kind == NativeInputEventKind.Wheel
                        ? (int)MathF.Round(valueY * 120) : 0);
            if (!handled && kind == NativeInputEventKind.PointerDown && code == 0)
                runtime.PointerDown(0, x, y, 1);
        }
        catch (Exception exception)
        {
            SharpLog.Error("EditorInput", "InputEvent failed", exception);
        }
    }

    [UnmanagedCallersOnly]
    public static int RunMcp()
    {
        try
        {
            return McpStdioBridge.Run();
        }
        catch (Exception exception)
        {
            SharpLog.Error("MCP", "MCP failed", exception);
            return -1;
        }
    }

    private static void RegisterEditorInput(EditorRuntime runtime,
        InputRuntimeService input)
    {
        s_inputHandlers.Add(input.BindAction(InputRoute.Editor,
            "engine://input/actions/editor.play-pause", int.MaxValue, value =>
            {
                if (value.Phase != InputActionPhase.Performed) return false;
                runtime.InvokeUiAction(runtime.PlayState == EditorPlayState.Playing
                    ? "editor.pause" : "editor.play");
                return true;
            }));
        s_inputHandlers.Add(input.BindAction(InputRoute.Editor,
            "engine://input/actions/editor.stop", int.MaxValue, value =>
            {
                if (value.Phase != InputActionPhase.Performed) return false;
                runtime.InvokeUiAction("editor.stop");
                return true;
            }));
        s_inputHandlers.Add(input.BindAction(InputRoute.Editor,
            "engine://input/actions/editor.undo", int.MaxValue, value =>
            {
                if (value.Phase != InputActionPhase.Performed) return false;
                runtime.InvokeUiAction("editor.undo");
                return true;
            }));
        s_inputHandlers.Add(input.BindAction(InputRoute.Editor,
            "engine://input/actions/editor.redo", int.MaxValue, value =>
            {
                if (value.Phase != InputActionPhase.Performed) return false;
                runtime.InvokeUiAction("editor.redo");
                return true;
            }));
    }

    private static void DisposeInputHandlers()
    {
        for (var index = s_inputHandlers.Count - 1; index >= 0; index--)
            s_inputHandlers[index].Dispose();
        s_inputHandlers.Clear();
    }
}
