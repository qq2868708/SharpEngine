using System.Text;
using System.Runtime.InteropServices;
using SharpEngine.UI;
using SharpEngine.World;

namespace SharpEngine.Editor;

internal unsafe readonly struct NativeEngineApi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeEngineApiTable
    {
        public uint Version;
        public delegate* unmanaged[Cdecl]<nint, byte*, void>
            SetWindowTitle;
        public delegate* unmanaged[Cdecl]<nint, uint, uint, void> BeginUi;
        public delegate* unmanaged[Cdecl]<float, float, float, float,
            byte, byte, byte, byte, void> UiPanel;
        public delegate* unmanaged[Cdecl]<float, float, float, float, byte*,
            byte, byte, byte, byte, void> UiText;
        public delegate* unmanaged[Cdecl]<float, float, float, float, byte*,
            byte, byte, byte, byte, byte, void> UiButton;
        public delegate* unmanaged[Cdecl]<nint, void> EndUi;
        public delegate* unmanaged[Cdecl]<float, float, float, float, byte*,
            byte, byte, byte, byte, byte, void> UiInput;
        public delegate* unmanaged[Cdecl]<byte*, byte*, void> SetUiText;
        public delegate* unmanaged[Cdecl]<float*, float*, float*, float*, byte> GetViewportRect;
        public delegate* unmanaged[Cdecl]<float*, uint, float*, uint, void> SubmitViewportFrame;
        public delegate* unmanaged[Cdecl]<byte*, byte*, void> SetUiMarkup;
        public delegate* unmanaged[Cdecl]<byte*, byte*, byte*, void> SetUiProperty;
        public delegate* unmanaged[Cdecl]<nint, int, int, uint, uint, int, void> ViewportInput;
    }

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

    public bool HasDocumentUi => _setUiText != null && _getViewportRect != null;

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
        fixed (WorldViewportVertex* triangles = frame.Triangles)
        fixed (WorldViewportVertex* lines = frame.Lines)
            _submitViewportFrame((float*)triangles, (uint)frame.Triangles.Length,
                (float*)lines, (uint)frame.Lines.Length);
    }

}
