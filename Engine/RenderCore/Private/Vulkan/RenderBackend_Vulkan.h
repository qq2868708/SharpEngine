#pragma once
#include "../RenderBackend.h"

class RenderBackend_Vulkan : public RenderBackend
{
public:
    bool Initialize(const RenderInitDesc* desc) override;
    void Shutdown() override;

    void BeginFrame() override;
    void SubmitFrame(const RenderFrame* frame) override;
    void EndFrame() override;

    void Resize(uint32_t width, uint32_t height) override;
};
