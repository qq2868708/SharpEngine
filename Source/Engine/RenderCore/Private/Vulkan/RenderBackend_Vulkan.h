#pragma once
#include "../RenderBackend.h"
#include <memory>

class RenderBackend_Vulkan : public RenderBackend
{
public:
    RenderBackend_Vulkan();
    ~RenderBackend_Vulkan() override;

    bool Initialize(const RenderInitDesc* desc) override;
    void Shutdown() override;

    void BeginFrame() override;
    void SubmitFrame(const RenderFrame* frame) override;
    void EndFrame() override;

    void Resize(uint32_t width, uint32_t height) override;
    void RequestScreenshot(const wchar_t* path) override;
    Rml::RenderInterface* GetUiRenderer() override;
    bool MakeCurrent() override;
    uint32_t GetUiDrawCalls() const override;
    bool WasPresented() const override;
    bool ReadPixels(std::vector<unsigned char>& pixels, uint32_t& width, uint32_t& height) override;
    RenderBackendStats GetStats() const override { return Stats; }

private:
    struct Surface;
    std::unique_ptr<Surface> State;
    RenderBackendStats Stats;
};
