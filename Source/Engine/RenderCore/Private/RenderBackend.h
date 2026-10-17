#pragma once
#include <stdint.h>

struct RenderInitDesc;
struct RenderFrame;

class RenderBackend
{
public:
    virtual ~RenderBackend() = default;

    virtual bool Initialize(const RenderInitDesc* desc) = 0;
    virtual void Shutdown() = 0;

    virtual void BeginFrame() = 0;
    virtual void SubmitFrame(const RenderFrame* frame) = 0;
    virtual void EndFrame() = 0;

    virtual void Resize(uint32_t width, uint32_t height) = 0;
    virtual void RequestScreenshot(const wchar_t* path) = 0;
    virtual bool HandleWindowMessage(void* window, uint32_t message,
                                     uintptr_t wParam, intptr_t lParam)
    {
        (void)window; (void)message; (void)wParam; (void)lParam;
        return false;
    }
    virtual bool IsUiFocused() const { return false; }
};
