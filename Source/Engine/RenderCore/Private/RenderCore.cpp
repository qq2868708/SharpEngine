#include "RenderCore/Public/RenderCore.h"
#include "RenderBackend.h"
#include "OpenGL/RenderBackend_OpenGL.h"
#include "Vulkan/RenderBackend_Vulkan.h"
#include "RenderBackend_Offscreen.h"
#include "RenderBackend_NullRHI.h"

static std::unique_ptr<RenderBackend> GBackend;

std::unique_ptr<RenderBackend> CreateRenderBackend(const RenderInitDesc& desc)
{
    std::unique_ptr<RenderBackend> backend;
    switch (desc.Backend)
    {
    case RenderBackendType::Vulkan:
        backend = std::make_unique<RenderBackend_Vulkan>();
        break;
    case RenderBackendType::Offscreen:
        backend = std::make_unique<RenderBackend_Offscreen>();
        break;
    case RenderBackendType::NullRHI:
        backend = std::make_unique<RenderBackend_NullRHI>();
        break;
    case RenderBackendType::OpenGL:
        backend = std::make_unique<RenderBackend_OpenGL>();
        break;
    default:
        return nullptr;
    }
    if (!backend->Initialize(&desc))
    {
        return nullptr;
    }
    return backend;
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
}
