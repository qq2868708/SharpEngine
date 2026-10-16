#pragma once
#include "Platform/PlatformTypes.h"
#include <windows.h>

using Win32PaintCallback = void(__cdecl*)(HWND window, HDC dc,
                                          const RECT* paintRect);
using Win32MouseButtonCallback = void(__cdecl*)(HWND window, int x, int y,
                                                WPARAM button);

class Win32Window
{
public:
    bool Create(uint32_t width, uint32_t height, const wchar_t* title);
    void Destroy();

    bool PollEvents();

    void SetCallbacks(Win32PaintCallback paintCallback,
                      Win32MouseButtonCallback mouseButtonCallback);
    bool HasPaintCallback() const { return PaintCallback != nullptr; }
    bool HasMouseButtonCallback() const { return MouseButtonCallback != nullptr; }
    void InvokePaint(HWND window, HDC dc, const RECT* paintRect) const
    {
        if (PaintCallback) PaintCallback(window, dc, paintRect);
    }
    void InvokeMouseButton(HWND window, int x, int y, WPARAM button) const
    {
        if (MouseButtonCallback) MouseButtonCallback(window, x, y, button);
    }

    HWND GetHWND() const { return hWnd; }
    HINSTANCE GetInstance() const { return hInstance; }

private:
    HWND hWnd = nullptr;
    HINSTANCE hInstance = nullptr;
    Win32PaintCallback PaintCallback = nullptr;
    Win32MouseButtonCallback MouseButtonCallback = nullptr;
};
