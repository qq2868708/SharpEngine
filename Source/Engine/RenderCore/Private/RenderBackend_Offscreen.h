#pragma once

#include "RenderBackend.h"
#include <vector>

// A deterministic CPU framebuffer for RenderOffscreen validation. It consumes
// the renderer-neutral scene vertices and does not create a window, GL context,
// Vulkan instance, swapchain, or other native graphics resource.
class RenderBackend_Offscreen final : public RenderBackend
{
public:
    ~RenderBackend_Offscreen() override { Shutdown(); }
    bool Initialize(const RenderInitDesc* desc) override;
    void Shutdown() override;

    void BeginFrame() override;
    void SubmitFrame(const RenderFrame* frame) override;
    void EndFrame() override;

    void Resize(uint32_t width, uint32_t height) override;
    void RequestScreenshot(const wchar_t* path) override;
    RenderBackendStats GetStats() const override { return Stats; }

private:
    void Clear();
    void DrawTriangle(const RenderVertex& first, const RenderVertex& second,
                      const RenderVertex& third, const RenderFrame& frame);
    void DrawLine(const RenderVertex& first, const RenderVertex& second,
                  const RenderFrame& frame);
    void PutPixel(int x, int y, float depth, float r, float g, float b,
                  float a);

    uint32_t Width = 0;
    uint32_t Height = 0;
    std::vector<uint8_t> Pixels;
    std::vector<float> Depth;
    RenderBackendStats Stats;
    bool Initialized = false;
};
