#pragma once
#include <stdint.h>

#ifdef _WIN32
#define RC_API __declspec(dllexport)
#else
#define RC_API
#endif

extern "C"
{
    struct RenderInitDesc
    {
        void* WindowHandle;     // GLFWwindow*
        uint32_t Width;
        uint32_t Height;
        bool EnableValidation;
    };

    struct RenderFrame
    {
        bool DrawTriangle;      // 临时代码
    };

    RC_API bool RC_Initialize(const RenderInitDesc* desc);
    RC_API void RC_Shutdown();

    RC_API void RC_BeginFrame();
    RC_API void RC_SubmitFrame(const RenderFrame* frame);
    RC_API void RC_EndFrame();

    RC_API void RC_Resize(uint32_t width, uint32_t height);
}
