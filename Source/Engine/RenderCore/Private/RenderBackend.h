#pragma once
#include <stdint.h>
#include <memory>
#include <vector>
#include "RenderBackend_Stats.h"
#include "../Public/GpuScene.h"

namespace Rml
{
    class RenderInterface;
}

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
    virtual bool EnsureGpuMesh(const GpuMeshUpload*) { return false; }
    virtual bool SubmitGpuScene(const GpuSceneFrame*) { return false; }
    virtual bool SupportsGpuScene() const { return false; }
    virtual void EndFrame() = 0;

    virtual void Resize(uint32_t width, uint32_t height) = 0;
    virtual void RequestScreenshot(const wchar_t* path) = 0;
    virtual RenderBackendStats GetStats() const { return {}; }
    // A window surface owns its GPU context and UI renderer. Hosts own the
    // RmlUi contexts/documents and render them between BeginFrame/EndFrame.
    virtual Rml::RenderInterface* GetUiRenderer() { return nullptr; }
    virtual bool MakeCurrent() { return true; }
    virtual uint32_t GetUiDrawCalls() const { return 0; }
    virtual bool WasPresented() const { return true; }
    virtual void RequestReadback() {}
    // BGRA8 rows, bottom to top, matching the native UI frame contract.
    virtual bool ReadPixels(std::vector<unsigned char>&, uint32_t&, uint32_t&) { return false; }
};

std::unique_ptr<RenderBackend> CreateRenderBackend(const RenderInitDesc& desc);
RenderBackend* GetActiveRenderBackend();
