#pragma once

#include "../RenderBackend.h"

class RenderBackend_OpenGL final : public RenderBackend
{
public:
    RenderBackend_OpenGL();
    ~RenderBackend_OpenGL() override;
    bool Initialize(const RenderInitDesc* desc) override;
    void Shutdown() override;

    void BeginFrame() override;
    void SubmitFrame(const RenderFrame* frame) override;
    bool EnsureGpuMesh(const GpuMeshUpload* Upload) override;
    bool SubmitGpuScene(const GpuSceneFrame* Frame) override;
    bool SupportsGpuScene() const override;
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
    bool Initialized = false;
    uint32_t Width = 0;
    uint32_t Height = 0;
    struct Surface;
    std::unique_ptr<Surface> State;
    RenderBackendStats Stats;
};
