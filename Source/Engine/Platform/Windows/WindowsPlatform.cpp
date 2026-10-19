#include "WindowsPlatform.h"
#include "Platform/Platform.h"
#include "Platform/Windows/Window/Win32Window.h"

static Win32Window* GMainWindow = nullptr;

namespace Platform
{
    void Initialize()
    {
        Initialize_Windows();
    }

    void Shutdown()
    {
        Shutdown_Windows();
    }

    EPlatformType GetPlatformType()
    {
        return EPlatformType::Windows;
    }

    NativeWindowHandle CreateMainWindow(
        uint32 width,
        uint32 height,
        const wchar_t* title,
        bool Visible)
    {
        GMainWindow = new Win32Window();
        GMainWindow->Create(width, height, title, Visible);

        NativeWindowHandle handle{};
        handle.Window   = (void*)GMainWindow->GetHWND();
        handle.Instance = (void*)GMainWindow->GetInstance();
        return handle;
    }

    bool PollEvents()
    {
        return GMainWindow ? GMainWindow->PollEvents() : false;
    }

    void SetMainWindowCallbacks(PlatformPaintCallback paintCallback,
                                PlatformMouseButtonCallback mouseButtonCallback)
    {
        if (GMainWindow)
        {
            GMainWindow->SetCallbacks(paintCallback, mouseButtonCallback);
        }
    }

    void Initialize_Windows()
    {
        // 未来：DPI / COM / Timer
    }

    void Shutdown_Windows()
    {
        if (GMainWindow)
        {
            GMainWindow->Destroy();
            delete GMainWindow;
            GMainWindow = nullptr;
        }
    }
}
