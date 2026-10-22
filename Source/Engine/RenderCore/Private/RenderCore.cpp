#include "RenderCore/Public/RenderCore.h"
#include "RenderBackend.h"
#include "OpenGL/RenderBackend_OpenGL.h"
#if !defined(SHARPENGINE_WITH_VULKAN) || SHARPENGINE_WITH_VULKAN
#include "Vulkan/RenderBackend_Vulkan.h"
#endif
#include "RenderBackend_Offscreen.h"
#include "RenderBackend_NullRHI.h"

static std::unique_ptr<RenderBackend> GBackend;
static RenderBackendType GBackendType = RenderBackendType::OpenGL;

std::unique_ptr<RenderBackend> CreateRenderBackend(const RenderInitDesc& Desc)
{
    std::unique_ptr<RenderBackend> Backend;
    switch (Desc.Backend)
    {
    case RenderBackendType::Vulkan:
#if !defined(SHARPENGINE_WITH_VULKAN) || SHARPENGINE_WITH_VULKAN
        Backend = std::make_unique<RenderBackend_Vulkan>();
        break;
#else
        return nullptr;
#endif
    case RenderBackendType::Offscreen:
        Backend = std::make_unique<RenderBackend_Offscreen>();
        break;
    case RenderBackendType::NullRHI:
        Backend = std::make_unique<RenderBackend_NullRHI>();
        break;
    case RenderBackendType::OpenGL:
        Backend = std::make_unique<RenderBackend_OpenGL>();
        break;
    default:
        return nullptr;
    }
    if (!Backend->Initialize(&Desc))
    {
        return nullptr;
    }
    return Backend;
}

RenderBackend* GetActiveRenderBackend() { return GBackend.get(); }

bool RC_Initialize(const RenderInitDesc* desc)
{
    RC_Shutdown();
    if (!desc)
    {
        return false;
    }
    GBackend = CreateRenderBackend(*desc);
    GBackendType = desc->Backend;
    return GBackend != nullptr;
}

void RC_Shutdown() { GBackend.reset(); }

void RC_BeginFrame()
{
    if (GBackend)
    {
        GBackend->BeginFrame();
    }
}

void RC_SubmitFrame(const RenderFrame* frame)
{
    if (GBackend)
    {
        GBackend->SubmitFrame(frame);
    }
}

bool RC_EnsureGpuMesh(const GpuMeshUpload* upload)
{
    return GBackend && GBackend->EnsureGpuMesh(upload);
}

bool RC_SubmitGpuScene(const GpuSceneFrame* frame)
{
    return GBackend && GBackend->SubmitGpuScene(frame);
}

void RC_EndFrame()
{
    if (GBackend)
    {
        GBackend->EndFrame();
    }
}

void RC_Resize(uint32_t width, uint32_t height)
{
    if (GBackend)
    {
        GBackend->Resize(width, height);
    }
}

void RC_RequestScreenshot(const wchar_t* path)
{
    if (GBackend)
    {
        GBackend->RequestScreenshot(path);
    }
}

void RC_GetStats(RenderStats* stats)
{
    if (!stats)
    {
        return;
    }
    *stats = GBackend ? GBackend->GetStats().Public() : RenderStats{};
    stats->Backend = static_cast<uint32_t>(GBackendType);
    stats->SupportsGpuScene = GBackend && GBackend->SupportsGpuScene() ? 1 : 0;
}
