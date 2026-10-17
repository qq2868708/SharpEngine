#pragma once
#include <stdint.h>

#ifdef _WIN32
#define RC_API __declspec(dllexport)
#else
#define RC_API
#endif

extern "C"
{
    enum class RenderBackendType : uint32_t
    {
        OpenGL = 0,
        Vulkan = 1,
    };

    struct RenderInitDesc
    {
        // The active native window. On Win32 this is an HWND. The backend
        // uses the current graphics context and does not retain this pointer.
        void* WindowHandle;
        uint32_t Width;
        uint32_t Height;
        bool EnableValidation;
        RenderBackendType Backend = RenderBackendType::OpenGL;
    };

    struct RenderVertex
    {
        float X;
        float Y;
        float Z;
        float R;
        float G;
        float B;
        float A;
    };

    struct RenderDrawList
    {
        // The backend consumes these spans during RC_SubmitFrame and never
        // retains the managed/native address after the call returns.
        const RenderVertex* Vertices;
        uint32_t VertexCount;
    };

    struct RenderFrame
    {
        float ViewportX;
        float ViewportY;
        float ViewportWidth;
        float ViewportHeight;
        float ClientHeight;
        RenderDrawList Triangles;
        RenderDrawList Lines;
    };

    RC_API bool RC_Initialize(const RenderInitDesc* desc);
    RC_API void RC_Shutdown();

    RC_API void RC_BeginFrame();
    RC_API void RC_SubmitFrame(const RenderFrame* frame);
    RC_API void RC_EndFrame();

    RC_API void RC_Resize(uint32_t width, uint32_t height);
    RC_API void RC_RequestScreenshot(const wchar_t* path);
    // Routes a native window message through the active backend UI first.
    // Returns true when the UI consumed the message.
    RC_API bool RC_HandleWindowMessage(void* window, uint32_t message,
                                       uintptr_t wParam, intptr_t lParam);
    RC_API bool RC_IsUiFocused();
}
