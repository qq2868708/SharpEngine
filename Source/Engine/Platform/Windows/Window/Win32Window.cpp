#include "Win32Window.h"
#include "Platform/Windows/SharpEngineBranding.h"
#include <dwmapi.h>
#include <windowsx.h>

#pragma comment(lib, "dwmapi.lib")

static LRESULT BorderlessHitTest(HWND hwnd, POINT point)
{
    RECT client{};
    GetClientRect(hwnd, &client);
    if (!IsZoomed(hwnd))
    {
        const UINT dpi = GetDpiForWindow(hwnd);
        const int padding = GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi);
        const int borderX = GetSystemMetricsForDpi(SM_CXSIZEFRAME, dpi) + padding;
        const int borderY = GetSystemMetricsForDpi(SM_CYSIZEFRAME, dpi) + padding;
        const bool left = point.x < borderX;
        const bool right = point.x >= client.right - borderX;
        const bool top = point.y < borderY;
        const bool bottom = point.y >= client.bottom - borderY;
        if (top && left) return HTTOPLEFT;
        if (top && right) return HTTOPRIGHT;
        if (bottom && left) return HTBOTTOMLEFT;
        if (bottom && right) return HTBOTTOMRIGHT;
        if (left) return HTLEFT;
        if (right) return HTRIGHT;
        if (top) return HTTOP;
        if (bottom) return HTBOTTOM;
    }
    // RmlUi identifies draggable title-bar space from the actual UI layout.
    return HTCLIENT;
}

static LRESULT CALLBACK WindowProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam)
{
    auto* window = reinterpret_cast<Win32Window*>(GetWindowLongPtrW(
        hwnd, GWLP_USERDATA));
    if (msg == WM_NCCREATE)
    {
        const auto* create = reinterpret_cast<const CREATESTRUCTW*>(lParam);
        window = static_cast<Win32Window*>(create->lpCreateParams);
        SetWindowLongPtrW(hwnd, GWLP_USERDATA,
                          reinterpret_cast<LONG_PTR>(window));
    }

    if (window && msg == WM_ERASEBKGND && window->HasPaintCallback())
    {
        return 1;
    }
    if (window && msg == WM_PAINT && window->HasPaintCallback())
    {
        PAINTSTRUCT paint{};
        const HDC dc = BeginPaint(hwnd, &paint);
        window->InvokePaint(hwnd, dc, &paint.rcPaint);
        EndPaint(hwnd, &paint);
        return 0;
    }
    if (window && msg == WM_LBUTTONDOWN)
    {
        window->InvokeMouseButton(hwnd, GET_X_LPARAM(lParam),
                                  GET_Y_LPARAM(lParam), wParam);
        if (window->HasMouseButtonCallback()) return 0;
    }

    // The client area fills the entire borderless window. Return native hit
    // targets for resize grips so the
    // standard move/resize behavior remains available without a white frame.
    if (window && msg == WM_NCHITTEST)
    {
        POINT point{GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam)};
        ScreenToClient(hwnd, &point);
        return BorderlessHitTest(hwnd, point);
    }

    if (msg == WM_NCCALCSIZE)
    {
        // Remove the native non-client frame while retaining WS_THICKFRAME's
        // system resize semantics through the custom hit-test above.
        return 0;
    }

    if (msg == WM_GETMINMAXINFO)
    {
        // A maximized borderless window must fit the monitor's work area,
        // including when the taskbar is on a secondary monitor.
        MONITORINFO monitor{sizeof(MONITORINFO)};
        if (GetMonitorInfoW(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), &monitor))
        {
            auto* bounds = reinterpret_cast<MINMAXINFO*>(lParam);
            bounds->ptMaxPosition.x = monitor.rcWork.left - monitor.rcMonitor.left;
            bounds->ptMaxPosition.y = monitor.rcWork.top - monitor.rcMonitor.top;
            bounds->ptMaxSize.x = monitor.rcWork.right - monitor.rcWork.left;
            bounds->ptMaxSize.y = monitor.rcWork.bottom - monitor.rcWork.top;
            return 0;
        }
    }

    if (msg == WM_CLOSE || msg == WM_DESTROY)
    {
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProc(hwnd, msg, wParam, lParam);
}

bool Win32Window::Create(uint32_t width, uint32_t height, const wchar_t* title)
{
    hInstance = GetModuleHandle(nullptr);
    WNDCLASS wc{};
    wc.lpfnWndProc = WindowProc;
    wc.style = CS_DBLCLKS;
    wc.hInstance = hInstance;
    wc.lpszClassName = L"SelfEngineWindow";
    wc.hIcon = SharpEngine::Windows::LoadBrandIcon(
        hInstance, GetSystemMetrics(SM_CXICON), GetSystemMetrics(SM_CYICON));
    wc.hCursor = LoadCursor(nullptr, IDC_ARROW);

    RegisterClass(&wc);

    // The editor renders its own dark title/menu bar. Keep WS_THICKFRAME for
    // native resizing, but remove the non-client frame in WM_NCCALCSIZE so no
    // white caption or border is drawn around the client area.
    constexpr DWORD windowStyle = WS_OVERLAPPEDWINDOW;
    constexpr DWORD windowExStyle = WS_EX_APPWINDOW;

    hWnd = CreateWindowEx(
        windowExStyle, wc.lpszClassName, title,
        windowStyle | WS_VISIBLE,
        CW_USEDEFAULT, CW_USEDEFAULT,
        static_cast<int>(width), static_cast<int>(height),
        nullptr, nullptr, hInstance, this
    );

    if (!hWnd) return false;
    SharpEngine::Windows::ApplyBrandIcon(hWnd, hInstance);

    // Windows 11 can still draw a one-pixel DWM outline after the non-client
    // area is removed. Suppress that outline as well (ignored on older OSes).
    const COLORREF noBorder = DWMWA_COLOR_NONE;
    DwmSetWindowAttribute(hWnd, DWMWA_BORDER_COLOR, &noBorder, sizeof(noBorder));
    return true;
}

void Win32Window::SetCallbacks(Win32PaintCallback paintCallback,
                                Win32MouseButtonCallback mouseButtonCallback)
{
    PaintCallback = paintCallback;
    MouseButtonCallback = mouseButtonCallback;
    if (hWnd)
    {
        InvalidateRect(hWnd, nullptr, FALSE);
    }
}

void Win32Window::Destroy()
{
    if (hWnd)
    {
        DestroyWindow(hWnd);
        hWnd = nullptr;
    }
}

bool Win32Window::PollEvents()
{
    MSG msg;
    while (PeekMessage(&msg, nullptr, 0, 0, PM_REMOVE))
    {
        TranslateMessage(&msg);
        DispatchMessage(&msg);

        if (msg.message == WM_QUIT)
            return false;
    }
    return true;
}
