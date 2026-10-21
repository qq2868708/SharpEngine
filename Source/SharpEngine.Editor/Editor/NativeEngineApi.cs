using SharpEngine.Rendering;
using SharpEngine.Interop;
using System.Text;
using System.Runtime.InteropServices;
using SharpEngine.UI;
using SharpEngine.World;

namespace SharpEngine.Editor;

internal unsafe readonly struct NativeEngineApi
{
    private readonly delegate* unmanaged[Cdecl]<nint, byte*, void>
        _setWindowTitle;
    private readonly delegate* unmanaged[Cdecl]<nint, uint, uint, void> _beginUi;
    private readonly delegate* unmanaged[Cdecl]<float, float, float, float,
        byte, byte, byte, byte, void> _uiPanel;
    private readonly delegate* unmanaged[Cdecl]<float, float, float, float,
        byte*, byte, byte, byte, byte, void> _uiText;
    private readonly delegate* unmanaged[Cdecl]<float, float, float, float,
        byte*, byte, byte, byte, byte, byte, void> _uiButton;
    private readonly delegate* unmanaged[Cdecl]<nint, void> _endUi;
    private readonly delegate* unmanaged[Cdecl]<float, float, float, float,
        byte*, byte, byte, byte, byte, byte, void> _uiInput;
    private readonly delegate* unmanaged[Cdecl]<byte*, byte*, void> _setUiText;
    private readonly delegate* unmanaged[Cdecl]<float*, float*, float*, float*, byte> _getViewportRect;
    private readonly delegate* unmanaged[Cdecl]<float*, uint, float*, uint, void> _submitViewportFrame;
    private readonly delegate* unmanaged[Cdecl]<byte*, byte*, void> _setUiMarkup;
    private readonly delegate* unmanaged[Cdecl]<byte*, byte*, byte*, void> _setUiProperty;
    private readonly delegate* unmanaged[Cdecl]<nint, int, int, uint, uint, int, void> _viewportInput;
    private readonly delegate* unmanaged[Cdecl]<byte*, byte*, nint> _createDockWindow;
    private readonly delegate* unmanaged[Cdecl]<byte*, byte> _destroyDockWindow;
    private readonly delegate* unmanaged[Cdecl]<byte*, byte*, byte*, byte> _setHostMarkup;
    private readonly delegate* unmanaged[Cdecl]<nint, byte*, byte*, uint, byte> _chooseWorldSavePath;
    private readonly delegate* unmanaged[Cdecl]<ulong, ulong, byte*, uint, uint> _getUiSnapshot;
    private readonly delegate* unmanaged[Cdecl]<byte*, uint, int, int, uint, uint,
        uint, uint, ulong, float, float, byte*, byte*, uint, uint> _uiPointer;
    private readonly delegate* unmanaged[Cdecl]<byte*, uint, uint, uint, byte*, ulong,
        byte*, uint, uint> _uiKey;
    private readonly delegate* unmanaged[Cdecl]<ulong, byte*, byte*, uint, uint*, uint*, uint>
        _readUiFrame;
    private readonly delegate* unmanaged[Cdecl]<byte*, float*, float*, float*, float*, byte>
        GetUiElementRectFunction;
    private readonly delegate* unmanaged[Cdecl]<float*, uint, float*, uint, void>
        SubmitTimelinePreviewFrameFunction;
    private readonly delegate* unmanaged[Cdecl]<byte*, byte*, nint>
        CreateEditorWindowFunction;
    private readonly delegate* unmanaged[Cdecl]<byte*, byte*, byte*, nint>
        CreateOwnedDockWindowFunction;
    private readonly delegate* unmanaged[Cdecl]<byte*, byte>
        DestroyEditorWindowFunction;
    private readonly delegate* unmanaged[Cdecl]<byte*, byte*, float*, float*, float*,
        float*, byte> GetHostUiElementRectFunction;
    private readonly delegate* unmanaged[Cdecl]<byte*, byte*, float*, uint, float*,
        uint, void> SubmitHostViewportFrameFunction;
    private readonly delegate* unmanaged[Cdecl]<byte, byte> SetGameplayInputCaptureFunction;
    private readonly delegate* unmanaged[Cdecl]<byte> GetGameplayInputCaptureFunction;

    public bool HasDocumentUi => _setUiText != null && _getViewportRect != null;
    public bool HasDockHosts => _createDockWindow != null && _destroyDockWindow != null &&
        _setHostMarkup != null;
    public bool HasUiObservability => _getUiSnapshot != null && _uiPointer != null &&
        _uiKey != null && _readUiFrame != null;

    public NativeEngineApi(nint table)
    {
        this = default;
        if (table == 0)
        {
            _setWindowTitle = null;
            _beginUi = null;
            _uiPanel = null;
            _uiText = null;
            _uiButton = null;
            _endUi = null;
            _uiInput = null;
            return;
        }

        var api = (NativeEngineApiTable*)table;
        _setWindowTitle = api->SetWindowTitle;
        if (api->Version < 2)
        {
            _beginUi = null;
            _uiPanel = null;
            _uiText = null;
            _uiButton = null;
            _endUi = null;
            _uiInput = null;
            return;
        }

        _beginUi = api->BeginUi;
        _uiPanel = api->UiPanel;
        _uiText = api->UiText;
        _uiButton = api->UiButton;
        _endUi = api->EndUi;
        _uiInput = api->Version >= 3 ? api->UiInput : null;
        if (api->Version >= 4)
        {
            _setUiText = api->SetUiText;
            _getViewportRect = api->GetViewportRect;
            _submitViewportFrame = api->SubmitViewportFrame;
            _setUiMarkup = api->SetUiMarkup;
        }
        if (api->Version >= 6)
            _setUiProperty = api->SetUiProperty;
        if (api->Version >= 5)
            _viewportInput = api->ViewportInput;
        if (api->Version >= 7)
        {
            _createDockWindow = api->CreateDockWindow;
            _destroyDockWindow = api->DestroyDockWindow;
            _setHostMarkup = api->SetHostMarkup;
        }
        if (api->Version >= 8)
            _chooseWorldSavePath = api->ChooseWorldSavePath;
        if (api->Version >= 9)
        {
            _getUiSnapshot = api->GetUiSnapshot;
            _uiPointer = api->UiPointer;
            _uiKey = api->UiKey;
            _readUiFrame = api->ReadUiFrame;
        }
        if (api->Version >= 11)
        {
            GetUiElementRectFunction = api->GetUiElementRect;
            SubmitTimelinePreviewFrameFunction = api->SubmitTimelinePreviewFrame;
        }
        if (api->Version >= 12)
        {
            CreateEditorWindowFunction = api->CreateEditorWindow;
            CreateOwnedDockWindowFunction = api->CreateOwnedDockWindow;
            DestroyEditorWindowFunction = api->DestroyEditorWindow;
            GetHostUiElementRectFunction = api->GetHostUiElementRect;
            SubmitHostViewportFrameFunction = api->SubmitHostViewportFrame;
        }
        if (api->Version >= 17)
        {
            SetGameplayInputCaptureFunction = api->SetGameplayInputCapture;
            GetGameplayInputCaptureFunction = api->GetGameplayInputCapture;
        }
    }

    internal bool SetGameplayInputCapture(bool Enabled)
    {
        return SetGameplayInputCaptureFunction != null &&
            SetGameplayInputCaptureFunction(Enabled ? (byte)1 : (byte)0) != 0;
    }

    internal bool IsGameplayInputCaptured()
    {
        return GetGameplayInputCaptureFunction != null && GetGameplayInputCaptureFunction() != 0;
    }

    public void SetWindowTitle(nint windowHandle, string title)
    {
        if (_setWindowTitle == null)
        {
            return;
        }

        var utf8 = Encoding.UTF8.GetBytes(title + "\0");
        fixed (byte* titlePointer = utf8)
        {
            _setWindowTitle(windowHandle, titlePointer);
        }
    }

    public void BeginUi(nint windowHandle, uint width, uint height)
    {
        if (_beginUi != null)
        {
            _beginUi(windowHandle, width, height);
        }
    }

    public void UiPanel(UiRect rect, UiColor color)
    {
        if (_uiPanel != null)
        {
            _uiPanel(rect.X, rect.Y, rect.Width, rect.Height,
                color.R, color.G, color.B, color.A);
        }
    }

    public unsafe void UiText(UiRect rect, string value, UiColor color)
    {
        if (_uiText == null) return;
        var utf8 = Encoding.UTF8.GetBytes(value + "\0");
        fixed (byte* text = utf8)
        {
            _uiText(rect.X, rect.Y, rect.Width, rect.Height, text,
                color.R, color.G, color.B, color.A);
        }
    }

    public unsafe void UiButton(UiRect rect, string value, UiColor color,
        bool enabled)
    {
        if (_uiButton == null) return;
        var utf8 = Encoding.UTF8.GetBytes(value + "\0");
        fixed (byte* text = utf8)
        {
            _uiButton(rect.X, rect.Y, rect.Width, rect.Height, text,
                color.R, color.G, color.B, color.A, enabled ? (byte)1 : (byte)0);
        }
    }

    public unsafe void UiInput(UiRect rect, string value, UiColor color)
    {
        if (_uiInput == null) return;
        var utf8 = Encoding.UTF8.GetBytes(value + "\0");
        fixed (byte* text = utf8)
        {
            _uiInput(rect.X, rect.Y, rect.Width, rect.Height, text,
                color.R, color.G, color.B, color.A, 1);
        }
    }

    public void EndUi(nint windowHandle)
    {
        if (_endUi != null)
        {
            _endUi(windowHandle);
        }
    }

    public void SetUiText(string id, string value)
    {
        if (_setUiText == null) return;
        var idUtf8 = Encoding.UTF8.GetBytes(id + "\0");
        var valueUtf8 = Encoding.UTF8.GetBytes(value + "\0");
        fixed (byte* idPointer = idUtf8)
        fixed (byte* valuePointer = valueUtf8)
            _setUiText(idPointer, valuePointer);
    }

    public void SetUiMarkup(string id, string markup)
    {
        if (_setUiMarkup == null) return;
        var idUtf8 = Encoding.UTF8.GetBytes(id + "\0");
        var markupUtf8 = Encoding.UTF8.GetBytes(markup + "\0");
        fixed (byte* idPointer = idUtf8)
        fixed (byte* markupPointer = markupUtf8)
            _setUiMarkup(idPointer, markupPointer);
    }

    public void SetUiProperty(string id, string name, string value)
    {
        if (_setUiProperty == null) return;
        var idUtf8 = Encoding.UTF8.GetBytes(id + "\0");
        var nameUtf8 = Encoding.UTF8.GetBytes(name + "\0");
        var valueUtf8 = Encoding.UTF8.GetBytes(value + "\0");
        fixed (byte* idPointer = idUtf8)
        fixed (byte* namePointer = nameUtf8)
        fixed (byte* valuePointer = valueUtf8)
            _setUiProperty(idPointer, namePointer, valuePointer);
    }

    public nint CreateDockWindow(string hostId, string title)
    {
        if (_createDockWindow == null) return 0;
        var hostUtf8 = Encoding.UTF8.GetBytes(hostId + "\0");
        var titleUtf8 = Encoding.UTF8.GetBytes(title + "\0");
        fixed (byte* hostPointer = hostUtf8)
        fixed (byte* titlePointer = titleUtf8)
            return _createDockWindow(hostPointer, titlePointer);
    }

    public bool DestroyDockWindow(string hostId)
    {
        if (_destroyDockWindow == null) return false;
        var hostUtf8 = Encoding.UTF8.GetBytes(hostId + "\0");
        fixed (byte* hostPointer = hostUtf8)
            return _destroyDockWindow(hostPointer) != 0;
    }

    public nint CreateEditorWindow(string HostId, string Title)
    {
        if (CreateEditorWindowFunction == null)
        {
            return 0;
        }

        var HostUtf8 = Encoding.UTF8.GetBytes(HostId + "\0");
        var TitleUtf8 = Encoding.UTF8.GetBytes(Title + "\0");
        fixed (byte* HostPointer = HostUtf8)
        fixed (byte* TitlePointer = TitleUtf8)
        {
            return CreateEditorWindowFunction(HostPointer, TitlePointer);
        }
    }

    public nint CreateOwnedDockWindow(string RootId, string HostId, string Title)
    {
        if (CreateOwnedDockWindowFunction == null)
        {
            return 0;
        }

        var RootUtf8 = Encoding.UTF8.GetBytes(RootId + "\0");
        var HostUtf8 = Encoding.UTF8.GetBytes(HostId + "\0");
        var TitleUtf8 = Encoding.UTF8.GetBytes(Title + "\0");
        fixed (byte* RootPointer = RootUtf8)
        fixed (byte* HostPointer = HostUtf8)
        fixed (byte* TitlePointer = TitleUtf8)
        {
            return CreateOwnedDockWindowFunction(RootPointer, HostPointer, TitlePointer);
        }
    }

    public bool DestroyEditorWindow(string RootId)
    {
        if (DestroyEditorWindowFunction == null)
        {
            return false;
        }

        var RootUtf8 = Encoding.UTF8.GetBytes(RootId + "\0");
        fixed (byte* RootPointer = RootUtf8)
        {
            return DestroyEditorWindowFunction(RootPointer) != 0;
        }
    }

    public bool SetHostMarkup(string hostId, string elementId, string markup)
    {
        if (_setHostMarkup == null)
        {
            if (string.Equals(hostId, "main", StringComparison.Ordinal))
            {
                SetUiMarkup(elementId, markup);
                return true;
            }
            return false;
        }
        var hostUtf8 = Encoding.UTF8.GetBytes(hostId + "\0");
        var idUtf8 = Encoding.UTF8.GetBytes(elementId + "\0");
        var markupUtf8 = Encoding.UTF8.GetBytes(markup + "\0");
        fixed (byte* hostPointer = hostUtf8)
        fixed (byte* idPointer = idUtf8)
        fixed (byte* markupPointer = markupUtf8)
            return _setHostMarkup(hostPointer, idPointer, markupPointer) != 0;
    }

    public bool TryChooseWorldSavePath(nint ownerWindow, string contentRoot,
        out string path)
    {
        path = string.Empty;
        if (_chooseWorldSavePath == null) return false;
        var rootUtf8 = Encoding.UTF8.GetBytes(contentRoot + "\0");
        var result = new byte[32768];
        fixed (byte* rootPointer = rootUtf8)
        fixed (byte* resultPointer = result)
        {
            if (_chooseWorldSavePath(ownerWindow, rootPointer, resultPointer,
                    (uint)result.Length) == 0) return false;
        }
        var end = Array.IndexOf(result, (byte)0);
        path = Encoding.UTF8.GetString(result, 0, end < 0 ? result.Length : end);
        return path.Length != 0;
    }

    public string DescribeUi(ulong layoutRevision, ulong documentRevision)
    {
        if (_getUiSnapshot == null)
            return UnavailableUiJson("UI_SNAPSHOT_UNAVAILABLE",
                "The native UI observation API is unavailable");
        var required = _getUiSnapshot(layoutRevision, documentRevision, null, 0);
        if (required == 0)
            return UnavailableUiJson("UI_SNAPSHOT_UNAVAILABLE",
                "The native UI snapshot could not be read");
        var result = new byte[required];
        fixed (byte* pointer = result)
        {
            if (_getUiSnapshot(layoutRevision, documentRevision, pointer,
                    (uint)result.Length) != required)
                return UnavailableUiJson("UI_SNAPSHOT_UNAVAILABLE",
                    "The native UI snapshot changed while it was read");
        }
        return Utf8Result(result);
    }

    public string UiPointer(string hostId, uint inputEvent, int x, int y,
        uint button, uint buttons, uint modifiers, uint pointerId,
        ulong expectedFrameId, float wheelX, float wheelY,
        string? expectedElementId)
    {
        if (_uiPointer == null)
            return UnavailableUiJson("UI_SNAPSHOT_UNAVAILABLE",
                "The native UI input API is unavailable");
        var host = Encoding.UTF8.GetBytes(hostId + "\0");
        var expected = Encoding.UTF8.GetBytes((expectedElementId ?? string.Empty) + "\0");
        var result = new byte[128 * 1024];
        uint required;
        fixed (byte* hostPointer = host)
        fixed (byte* expectedPointer = expected)
        fixed (byte* resultPointer = result)
            required = _uiPointer(hostPointer, inputEvent, x, y, button,
                buttons, modifiers, pointerId, expectedFrameId, wheelX, wheelY,
                expectedPointer, resultPointer, (uint)result.Length);
        if (required == 0 || required > result.Length)
            return UnavailableUiJson("UI_SNAPSHOT_UNAVAILABLE",
                "The native UI input result exceeded its bounded response buffer");
        return Utf8Result(result, required);
    }

    public string UiKey(string hostId, uint inputEvent, uint key,
        uint modifiers, string text, ulong expectedFrameId)
    {
        if (_uiKey == null)
            return UnavailableUiJson("UI_SNAPSHOT_UNAVAILABLE",
                "The native UI key API is unavailable");
        var host = Encoding.UTF8.GetBytes(hostId + "\0");
        var textBytes = Encoding.UTF8.GetBytes(text + "\0");
        var result = new byte[128 * 1024];
        uint required;
        fixed (byte* hostPointer = host)
        fixed (byte* textPointer = textBytes)
        fixed (byte* resultPointer = result)
            required = _uiKey(hostPointer, inputEvent, key, modifiers,
                textPointer, expectedFrameId, resultPointer, (uint)result.Length);
        if (required == 0 || required > result.Length)
            return UnavailableUiJson("UI_SNAPSHOT_UNAVAILABLE",
                "The native UI key result exceeded its bounded response buffer");
        return Utf8Result(result, required);
    }

    public NativeUiFrame? ReadUiFrame(ulong frameId, string hostId)
    {
        if (_readUiFrame == null) return null;
        var host = Encoding.UTF8.GetBytes(hostId + "\0");
        uint width = 0, height = 0;
        uint required;
        fixed (byte* hostPointer = host)
            required = _readUiFrame(frameId, hostPointer, null, 0, &width, &height);
        if (required == 0 || width == 0 || height == 0) return null;
        var pixels = new byte[required];
        fixed (byte* hostPointer = host)
        fixed (byte* pixelPointer = pixels)
        {
            var copied = _readUiFrame(frameId, hostPointer, pixelPointer,
                (uint)pixels.Length, &width, &height);
            if (copied != required) return null;
        }
        return new NativeUiFrame(width, height, pixels);
    }

    private static string Utf8Result(byte[] bytes, uint required = 0)
    {
        var count = required == 0 ? bytes.Length : checked((int)required);
        if (count > 0 && bytes[count - 1] == 0) count--;
        return Encoding.UTF8.GetString(bytes, 0, count);
    }

    private static string UnavailableUiJson(string code, string message) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            accepted = false,
            diagnostics = new[]
            {
                new { code, severity = "error", message, retryable = true },
            },
        });

    public static UnsavedWorldChoice PromptUnsavedWorld(nint ownerWindow, string worldTitle)
    {
        const uint YesNoCancel = 0x00000003;
        const uint Warning = 0x00000030;
        const int Yes = 6;
        const int No = 7;
        var result = MessageBoxW(ownerWindow,
            $"Save changes to {worldTitle} before closing?", "SharpEngine",
            YesNoCancel | Warning);
        return result == Yes ? UnsavedWorldChoice.Save :
            result == No ? UnsavedWorldChoice.DontSave : UnsavedWorldChoice.Cancel;
    }

    public static void ExitMessageLoop(int exitCode = 0) => PostQuitMessage(exitCode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint windowHandle, string text,
        string caption, uint type);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    public void ViewportInput(nint windowHandle, int x, int y, uint inputEvent,
        uint buttons, int wheelDelta)
    {
        if (_viewportInput != null)
            _viewportInput(windowHandle, x, y, inputEvent, buttons, wheelDelta);
    }

    public bool TryGetViewportRect(out UiRect bounds)
    {
        bounds = default;
        if (_getViewportRect == null) return false;
        float x = 0, y = 0, width = 0, height = 0;
        if (_getViewportRect(&x, &y, &width, &height) == 0 ||
            width <= 0 || height <= 0) return false;
        bounds = new UiRect(x, y, width, height);
        return true;
    }

    public void SubmitViewportFrame(WorldViewportFrame frame)
    {
        if (_submitViewportFrame == null) return;
        // Native copies these spans before returning. No managed address is
        // retained across a frame or exposed as a C++/graphics object.
        fixed (ViewportVertex* triangles = frame.Triangles)
        fixed (ViewportVertex* lines = frame.Lines)
            _submitViewportFrame((float*)triangles, (uint)frame.Triangles.Length,
                (float*)lines, (uint)frame.Lines.Length);
    }

    public bool TryGetUiElementRect(string id, out UiRect bounds)
    {
        bounds = default;
        if (GetUiElementRectFunction == null)
        {
            return false;
        }

        var idUtf8 = Encoding.UTF8.GetBytes(id + "\0");
        float x = 0;
        float y = 0;
        float width = 0;
        float height = 0;
        fixed (byte* idPointer = idUtf8)
        {
            if (GetUiElementRectFunction(idPointer, &x, &y, &width, &height) == 0)
            {
                return false;
            }
        }
        if (!float.IsFinite(width) || !float.IsFinite(height) || width <= 0 || height <= 0)
        {
            return false;
        }

        bounds = new UiRect(x, y, width, height);
        return true;
    }

    public void SubmitTimelinePreviewFrame(WorldViewportFrame frame)
    {
        if (SubmitTimelinePreviewFrameFunction == null)
        {
            return;
        }

        fixed (ViewportVertex* triangles = frame.Triangles)
        fixed (ViewportVertex* lines = frame.Lines)
        {
            SubmitTimelinePreviewFrameFunction((float*)triangles,
                (uint)frame.Triangles.Length, (float*)lines,
                (uint)frame.Lines.Length);
        }
    }

    public bool TryGetHostUiElementRect(string HostId, string Id, out UiRect Bounds)
    {
        Bounds = default;
        if (GetHostUiElementRectFunction == null)
        {
            return false;
        }

        var HostUtf8 = Encoding.UTF8.GetBytes(HostId + "\0");
        var IdUtf8 = Encoding.UTF8.GetBytes(Id + "\0");
        float X = 0;
        float Y = 0;
        float Width = 0;
        float Height = 0;
        fixed (byte* HostPointer = HostUtf8)
        fixed (byte* IdPointer = IdUtf8)
        {
            if (GetHostUiElementRectFunction(HostPointer, IdPointer, &X, &Y,
                    &Width, &Height) == 0)
            {
                return false;
            }
        }
        if (!float.IsFinite(Width) || !float.IsFinite(Height) ||
            Width <= 0 || Height <= 0)
        {
            return false;
        }

        Bounds = new UiRect(X, Y, Width, Height);
        return true;
    }

    public void SubmitHostViewportFrame(string HostId, string Kind,
        ViewportFrame Frame)
    {
        if (SubmitHostViewportFrameFunction == null)
        {
            return;
        }

        var HostUtf8 = Encoding.UTF8.GetBytes(HostId + "\0");
        var KindUtf8 = Encoding.UTF8.GetBytes(Kind + "\0");
        fixed (byte* HostPointer = HostUtf8)
        fixed (byte* KindPointer = KindUtf8)
        fixed (ViewportVertex* Triangles = Frame.Triangles)
        fixed (ViewportVertex* Lines = Frame.Lines)
        {
            SubmitHostViewportFrameFunction(HostPointer, KindPointer,
                (float*)Triangles, (uint)Frame.Triangles.Length,
                (float*)Lines, (uint)Frame.Lines.Length);
        }
    }

}

internal enum UnsavedWorldChoice : byte
{
    Save,
    DontSave,
    Cancel,
}
