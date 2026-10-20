#pragma once
#include <stdint.h>
#include "GpuScene.h"

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
        // CPU-backed framebuffer used by bounded RenderOffscreen validation.
        // It does not require a native window or a graphics device.
        Offscreen = 2,
        // Headless no-op backend analogous to UE's NullRHI.
        NullRHI = 3,
    };

    struct RenderInitDesc
    {
        // The active native window. On Win32 this is an HWND. Offscreen and
        // NullRHI intentionally accept nullptr and never retain this pointer.
        void* WindowHandle;
        uint32_t Width;
        uint32_t Height;
        bool EnableValidation;
        RenderBackendType Backend = RenderBackendType::OpenGL;
        bool EnableVSync = true;
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

    struct RenderStats
    {
        uint64_t FramesBegun = 0;
        uint64_t FramesSubmitted = 0;
        uint64_t TriangleVertices = 0;
        uint64_t LineVertices = 0;
        uint32_t Width = 0;
        uint32_t Height = 0;
        uint8_t HasFramebuffer = 0;
        uint32_t Backend = static_cast<uint32_t>(RenderBackendType::OpenGL);
        uint8_t SupportsGpuScene = 0;
        uint8_t VSyncEnabled = 0;
        uint8_t ValidationEnabled = 0;
    };

    RC_API bool RC_Initialize(const RenderInitDesc* desc);
    RC_API void RC_Shutdown();

    RC_API void RC_BeginFrame();
    RC_API void RC_SubmitFrame(const RenderFrame* frame);
    RC_API bool RC_EnsureGpuMesh(const GpuMeshUpload* upload);
    RC_API bool RC_SubmitGpuScene(const GpuSceneFrame* frame);
    RC_API void RC_EndFrame();

    RC_API void RC_Resize(uint32_t width, uint32_t height);
    RC_API void RC_RequestScreenshot(const wchar_t* path);
    // Returns transient renderer evidence for bounded validation. The result
    // is never serialized into authored content.
    RC_API void RC_GetStats(RenderStats* stats);

}
