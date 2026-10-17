#pragma once
#include <cstdint>

namespace Platform
{
    using uint8  = uint8_t;
    using uint16 = uint16_t;
    using uint32 = uint32_t;
    using uint64 = uint64_t;

    struct NativeWindowHandle
    {
        void* Window;     // HWND / NSWindow* / X11 Window
        void* Instance;   // HINSTANCE / NSApplication* / Display*
    };
}
