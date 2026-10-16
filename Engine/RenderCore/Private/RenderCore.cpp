#include "RenderCore/Public/RenderCore.h"
#include "RenderBackend.h"
#include "Vulkan/RenderBackend_Vulkan.h"

static RenderBackend* GBackend = nullptr;

bool RC_Initialize(const RenderInitDesc* desc)
{
    GBackend = new RenderBackend_Vulkan();
    return GBackend->Initialize(desc);
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
