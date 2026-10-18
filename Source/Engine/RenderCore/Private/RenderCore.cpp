#include "RenderCore/Public/RenderCore.h"
#include "RenderBackend.h"
#include "OpenGL/RenderBackend_OpenGL.h"
#include "Vulkan/RenderBackend_Vulkan.h"
#include "RenderBackend_Offscreen.h"
#include "RenderBackend_NullRHI.h"

static RenderBackend* GBackend = nullptr;

bool RC_Initialize(const RenderInitDesc* desc)
{
    RC_Shutdown();
    RenderBackend* backend = nullptr;
    switch (desc ? desc->Backend : RenderBackendType::OpenGL)
    {
    case RenderBackendType::Vulkan:
        backend = new RenderBackend_Vulkan();
        break;
    case RenderBackendType::Offscreen:
        backend = new RenderBackend_Offscreen();
        break;
    case RenderBackendType::NullRHI:
        backend = new RenderBackend_NullRHI();
        break;
    case RenderBackendType::OpenGL:
    default:
        backend = new RenderBackend_OpenGL();
        break;
    }
    if (!backend->Initialize(desc))
    {
        delete backend;
        return false;
    }
    GBackend = backend;
    return true;
}

void RC_Shutdown()
{
    delete GBackend;
    GBackend = nullptr;
}

void RC_BeginFrame()
{
    if (GBackend) GBackend->BeginFrame();
}

void RC_SubmitFrame(const RenderFrame* frame)
{
    if (GBackend) GBackend->SubmitFrame(frame);
}

void RC_EndFrame()
{
    if (GBackend) GBackend->EndFrame();
}

void RC_Resize(uint32_t width, uint32_t height)
{
    if (GBackend) GBackend->Resize(width, height);
}

void RC_RequestScreenshot(const wchar_t* path)
{
    if (GBackend) GBackend->RequestScreenshot(path);
}

void RC_GetStats(RenderStats* stats)
{
    if (!stats) return;
    *stats = GBackend ? GBackend->GetStats().Public() : RenderStats{};
}

bool RC_HandleWindowMessage(void* window, uint32_t message,
                            uintptr_t wParam, intptr_t lParam)
{
    return GBackend && GBackend->HandleWindowMessage(window, message, wParam, lParam);
}

bool RC_IsUiFocused()
{
    return GBackend && GBackend->IsUiFocused();
}
