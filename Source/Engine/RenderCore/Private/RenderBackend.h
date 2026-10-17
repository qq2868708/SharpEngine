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
};
