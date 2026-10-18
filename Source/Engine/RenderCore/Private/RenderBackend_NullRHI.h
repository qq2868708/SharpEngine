#pragma once

#include "RenderBackend.h"

// Headless no-op backend analogous to UE's NullRHI. It still accepts frames
// and reports draw statistics, which lets lifecycle and scene generation tests
// run without a GPU or a framebuffer.
class RenderBackend_NullRHI final : public RenderBackend
{
public:
    ~RenderBackend_NullRHI() override { Shutdown(); }
    bool Initialize(const RenderInitDesc* desc) override;
    void Shutdown() override;

    void BeginFrame() override;
    void SubmitFrame(const RenderFrame* frame) override;
    void EndFrame() override;

    void Resize(uint32_t width, uint32_t height) override;
    void RequestScreenshot(const wchar_t* path) override;
    RenderBackendStats GetStats() const override { return Stats; }

private:
    RenderBackendStats Stats;
    bool Initialized = false;
};
