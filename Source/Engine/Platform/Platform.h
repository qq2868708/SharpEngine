#pragma once
#include "PlatformTypes.h"

#include <windows.h>

using PlatformPaintCallback = void(__cdecl*)(HWND window, HDC dc,
                                             const RECT* paintRect);
using PlatformMouseButtonCallback = void(__cdecl*)(HWND window, int x, int y,
                                                   WPARAM button);

namespace Platform
{
    enum class EPlatformType
    {
        Windows,
        Linux,
        Mac
    };

    // ==============================
    // 生命周期
    // ==============================
    void Initialize();
    void Shutdown();

    // ==============================
    // 平台信息
    // ==============================
    EPlatformType GetPlatformType();

    // ==============================
    // Window
    // ==============================
    NativeWindowHandle CreateMainWindow(
        uint32 width,
        uint32 height,
        const wchar_t* title,
        bool Visible = true
    );

    bool PollEvents();

    void SetMainWindowCallbacks(PlatformPaintCallback paintCallback,
                                PlatformMouseButtonCallback mouseButtonCallback);
}
