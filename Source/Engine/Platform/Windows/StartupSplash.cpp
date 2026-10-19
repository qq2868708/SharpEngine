#include "StartupSplash.h"
#include "Platform/Windows/SharpEngineBranding.h"
#include "Logging/EngineLog.h"
#include <algorithm>

namespace
{
    constexpr UINT ProgressMessage = WM_APP + 1;
    constexpr UINT CloseMessage = WM_APP + 2;

    std::wstring ToWide(const char* Text)
    {
        if (!Text || !*Text)
        {
            return {};
        }
        const int Length = MultiByteToWideChar(CP_UTF8, 0, Text, -1, nullptr, 0);
        if (Length <= 0)
        {
            return {};
        }
        std::wstring Result(static_cast<size_t>(Length), L'\0');
        MultiByteToWideChar(CP_UTF8, 0, Text, -1, Result.data(), Length);
        Result.pop_back();
        return Result;
    }

    void Fill(HDC Dc, const RECT& Rect, COLORREF Color)
    {
        const HBRUSH Brush = CreateSolidBrush(Color);
        FillRect(Dc, &Rect, Brush);
        DeleteObject(Brush);
    }

    void Text(HDC Dc, const std::wstring& Value, RECT Rect, int Size,
              int Weight, COLORREF Color, UINT Alignment = DT_LEFT)
    {
        const HFONT Font = CreateFontW(-Size, 0, 0, 0, Weight, FALSE, FALSE, FALSE,
            DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
            CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
        const HGDIOBJ Previous = SelectObject(Dc, Font);
        SetTextColor(Dc, Color);
        SetBkMode(Dc, TRANSPARENT);
        DrawTextW(Dc, Value.c_str(), static_cast<int>(Value.size()), &Rect,
            Alignment | DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS | DT_NOPREFIX);
        SelectObject(Dc, Previous);
        DeleteObject(Font);
    }
}

StartupSplash::StartupSplash(bool Enabled, const std::wstring& Project)
    : Project(Project.empty() ? L"Engine workspace" : Project)
{
    if (!Enabled)
    {
        return;
    }
    try
    {
        auto Initialized = Ready.get_future();
        UiThread = std::thread([this]()
        {
            Run();
        });
        Initialized.wait();
    }
    catch (const std::exception& Error)
    {
        EngineLog::Warning("Startup", std::string("Splash unavailable: ") + Error.what());
    }
}

StartupSplash::~StartupSplash()
{
    Close();
}

void StartupSplash::Report(int Percent, const char* Module, const char* Detail)
{
    // Percent is a completed-stage estimate, never advanced by a timer.
    {
        const std::lock_guard Lock(StateMutex);
        this->Percent = (std::max)(this->Percent, std::clamp(Percent, 0, 100));
        this->Module = ToWide(Module);
        this->Detail = ToWide(Detail);
    }
    EngineLog::Info("Startup", std::to_string(Percent) + "% | " +
        (Module ? Module : "") + " | " + (Detail ? Detail : ""));
    const HWND Window = Handle.load();
    if (Window)
    {
        PostMessageW(Window, ProgressMessage, 0, 0);
    }
}

void StartupSplash::Close()
{
    const HWND Window = Handle.load();
    if (Window)
    {
        PostMessageW(Window, CloseMessage, 0, 0);
    }
    if (UiThread.joinable())
    {
        UiThread.join();
    }
}

void StartupSplash::Run()
{
    SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    const HINSTANCE Instance = GetModuleHandleW(nullptr);
    WNDCLASSW Class{};
    Class.lpfnWndProc = WindowProcedure;
    Class.hInstance = Instance;
    Class.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    Class.lpszClassName = L"SharpEngineStartupSplash";
    RegisterClassW(&Class);

    POINT Cursor{};
    GetCursorPos(&Cursor);
    MONITORINFO Monitor{};
    Monitor.cbSize = sizeof(Monitor);
    GetMonitorInfoW(MonitorFromPoint(Cursor, MONITOR_DEFAULTTOPRIMARY), &Monitor);
    const UINT Dpi = GetDpiForSystem();
    const int Width = MulDiv(720, Dpi, 96);
    const int Height = MulDiv(420, Dpi, 96);
    const HWND Window = CreateWindowExW(WS_EX_TOOLWINDOW, Class.lpszClassName,
        L"SharpEngine Editor - Starting", WS_POPUP,
        Monitor.rcWork.left + (Monitor.rcWork.right - Monitor.rcWork.left - Width) / 2,
        Monitor.rcWork.top + (Monitor.rcWork.bottom - Monitor.rcWork.top - Height) / 2,
        Width, Height, nullptr, nullptr, Instance, this);
    Handle.store(Window);
    if (Window)
    {
        Icon = SharpEngine::Windows::LoadBrandIcon(Instance, MulDiv(128, Dpi, 96), MulDiv(128, Dpi, 96));
        SetTimer(Window, 1, 250, nullptr);
        ShowWindow(Window, SW_SHOWNORMAL);
        UpdateWindow(Window);
    }
    Ready.set_value();
    if (Window)
    {
        MSG Message{};
        while (GetMessageW(&Message, nullptr, 0, 0) > 0)
        {
            TranslateMessage(&Message);
            DispatchMessageW(&Message);
        }
    }
    Handle.store(nullptr);
    if (Icon)
    {
        DestroyIcon(Icon);
        Icon = nullptr;
    }
    UnregisterClassW(Class.lpszClassName, Instance);
}

void StartupSplash::Refresh(HWND Window)
{
    std::wstring Title;
    {
        const std::lock_guard Lock(StateMutex);
        Title = L"SharpEngine Editor - " + std::to_wstring(Percent) + L"% - " + Module;
    }
    SetWindowTextW(Window, Title.c_str());
    InvalidateRect(Window, nullptr, FALSE);
    UpdateWindow(Window);
}

LRESULT CALLBACK StartupSplash::WindowProcedure(HWND Window, UINT Message, WPARAM WParam, LPARAM LParam)
{
    auto* Splash = reinterpret_cast<StartupSplash*>(GetWindowLongPtrW(Window, GWLP_USERDATA));
    if (Message == WM_NCCREATE)
    {
        const auto* Create = reinterpret_cast<const CREATESTRUCTW*>(LParam);
        Splash = static_cast<StartupSplash*>(Create->lpCreateParams);
        SetWindowLongPtrW(Window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(Splash));
    }
    if (Splash)
    {
        switch (Message)
        {
        case WM_PAINT:
        {
            PAINTSTRUCT Paint{};
            const HDC Dc = BeginPaint(Window, &Paint);
            Splash->Paint(Window, Dc);
            EndPaint(Window, &Paint);
            return 0;
        }
        case WM_ERASEBKGND:
            return 1;
        case WM_PRINTCLIENT:
            Splash->Paint(Window, reinterpret_cast<HDC>(WParam));
            return 0;
        case WM_NCHITTEST:
            return HTCAPTION;
        case WM_TIMER:
        case ProgressMessage:
            Splash->Refresh(Window);
            return 0;
        case WM_DPICHANGED:
        {
            const auto* Bounds = reinterpret_cast<const RECT*>(LParam);
            SetWindowPos(Window, nullptr, Bounds->left, Bounds->top,
                Bounds->right - Bounds->left, Bounds->bottom - Bounds->top,
                SWP_NOACTIVATE | SWP_NOZORDER);
            return 0;
        }
        case WM_CLOSE:
            return 0;
        case CloseMessage:
            DestroyWindow(Window);
            return 0;
        case WM_DESTROY:
            PostQuitMessage(0);
            return 0;
        }
    }
    return DefWindowProcW(Window, Message, WParam, LParam);
}

void StartupSplash::Paint(HWND Window, HDC Target)
{
    int CurrentPercent;
    std::wstring CurrentModule, CurrentDetail;
    {
        const std::lock_guard Lock(StateMutex);
        CurrentPercent = Percent;
        CurrentModule = Module;
        CurrentDetail = Detail;
    }
    RECT Client{};
    GetClientRect(Window, &Client);
    const HDC Dc = CreateCompatibleDC(Target);
    const HBITMAP Buffer = CreateCompatibleBitmap(Target, Client.right, Client.bottom);
    const HGDIOBJ Previous = SelectObject(Dc, Buffer);
    const auto Scale = [&](int Value) { return MulDiv(Value, Client.right, 720); };
    const auto Rect = [&](int Left, int Top, int Right, int Bottom)
    {
        return RECT
        {
            Scale(Left), Scale(Top), Scale(Right), Scale(Bottom)
        };
    };

    Fill(Dc, Client, RGB(15, 20, 29));
    Fill(Dc, Rect(1, 1, 719, 255), RGB(19, 29, 43));
    Fill(Dc, Rect(44, 36, 82, 39), RGB(88, 210, 226));
    Text(Dc, L"SHARPENGINE / EDITOR", Rect(94, 24, 676, 50), Scale(12), FW_SEMIBOLD, RGB(158, 182, 205));
    DrawIconEx(Dc, Scale(38), Scale(76), Icon, Scale(128), Scale(128), 0, nullptr, DI_NORMAL);
    Text(Dc, L"SharpEngine", Rect(188, 93, 676, 153), Scale(44), FW_SEMIBOLD, RGB(240, 246, 252));
    Text(Dc, L"C R E A T E .   I T E R A T E .   P L A Y .", Rect(191, 156, 676, 183), Scale(11), FW_NORMAL, RGB(131, 161, 188));
    Text(Dc, Project, Rect(44, 213, 676, 242), Scale(14), FW_NORMAL, RGB(197, 212, 227));

    Text(Dc, CurrentModule, Rect(44, 274, 593, 304), Scale(18), FW_SEMIBOLD, RGB(232, 240, 248));
    Text(Dc, std::to_wstring(CurrentPercent) + L"%", Rect(598, 274, 676, 304), Scale(22), FW_SEMIBOLD, RGB(104, 219, 233), DT_RIGHT);
    Fill(Dc, Rect(44, 317, 676, 321), RGB(44, 57, 73));
    Fill(Dc, Rect(44, 317, 44 + 632 * CurrentPercent / 100, 321), RGB(88, 210, 226));
    Text(Dc, CurrentDetail, Rect(44, 334, 676, 360), Scale(13), FW_NORMAL, RGB(156, 177, 198));
    const auto Seconds = std::chrono::duration_cast<std::chrono::seconds>(std::chrono::steady_clock::now() - Started).count();
    Text(Dc, L"INITIALIZING EDITOR", Rect(44, 378, 480, 402), Scale(10), FW_NORMAL, RGB(106, 128, 150));
    Text(Dc, std::to_wstring(Seconds) + L"s", Rect(525, 378, 676, 402), Scale(11), FW_NORMAL, RGB(106, 128, 150), DT_RIGHT);

    BitBlt(Target, 0, 0, Client.right, Client.bottom, Dc, 0, 0, SRCCOPY);
    SelectObject(Dc, Previous);
    DeleteObject(Buffer);
    DeleteDC(Dc);
}
