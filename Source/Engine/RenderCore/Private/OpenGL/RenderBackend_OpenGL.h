#pragma once

#include "../RenderBackend.h"

class RenderBackend_OpenGL final : public RenderBackend
{
public:
    ~RenderBackend_OpenGL() override { Shutdown(); }
    bool Initialize(const RenderInitDesc* desc) override;
    void Shutdown() override;

    void BeginFrame() override;
    void SubmitFrame(const RenderFrame* frame) override;
    void EndFrame() override;

    void Resize(uint32_t width, uint32_t height) override;
    void RequestScreenshot(const wchar_t* path) override;

private:
    bool Initialized = false;
    uint32_t Width = 0;
    uint32_t Height = 0;
};
