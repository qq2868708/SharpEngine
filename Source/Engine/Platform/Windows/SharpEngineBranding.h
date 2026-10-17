#pragma once

#include "SharpEngineResource.h"
#include <windows.h>

namespace SharpEngine::Windows
{
    inline HICON LoadBrandIcon(HINSTANCE instance, int width, int height)
    {
        if (!instance) instance = GetModuleHandleW(nullptr);
        return static_cast<HICON>(LoadImageW(
            instance, MAKEINTRESOURCEW(IDI_SHARPENGINE), IMAGE_ICON,
            width, height, LR_DEFAULTCOLOR));
    }

    inline void ApplyBrandIcon(HWND window, HINSTANCE instance = nullptr)
    {
        if (!window) return;
        const HICON largeIcon = LoadBrandIcon(
            instance, GetSystemMetrics(SM_CXICON),
            GetSystemMetrics(SM_CYICON));
        if (largeIcon)
        {
            SendMessageW(window, WM_SETICON, ICON_BIG,
                         reinterpret_cast<LPARAM>(largeIcon));
        }
        const HICON smallIcon = LoadBrandIcon(
            instance, GetSystemMetrics(SM_CXSMICON),
            GetSystemMetrics(SM_CYSMICON));
        if (smallIcon)
        {
            SendMessageW(window, WM_SETICON, ICON_SMALL,
                         reinterpret_cast<LPARAM>(smallIcon));
        }
    }
}
