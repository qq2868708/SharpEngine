#include "RenderBackend_NullRHI.h"

#include "../../Logging/EngineLog.h"
#include "../Public/RenderCore.h"

bool RenderBackend_NullRHI::Initialize(const RenderInitDesc* desc)
{
    if (!desc || desc->Width == 0 || desc->Height == 0)
    {
        EngineLog::Error("RenderCore", "NullRHI requires a non-zero logical viewport size");
        return false;
    }
    Stats = {};
    Stats.Width = desc->Width;
    Stats.Height = desc->Height;
    Stats.HasFramebuffer = false;
    Initialized = true;
    EngineLog::Info("RenderCore", "NullRHI initialized without a graphics device or framebuffer");
    return true;
}

void RenderBackend_NullRHI::Shutdown()
{
    Initialized = false;
    Stats = {};
}

void RenderBackend_NullRHI::BeginFrame()
{
    if (Initialized) ++Stats.FramesBegun;
}

void RenderBackend_NullRHI::SubmitFrame(const RenderFrame* frame)
{
    if (!Initialized || !frame) return;
    ++Stats.FramesSubmitted;
    Stats.TriangleVertices += frame->Triangles.VertexCount;
    Stats.LineVertices += frame->Lines.VertexCount;
}

void RenderBackend_NullRHI::EndFrame()
{
}

void RenderBackend_NullRHI::Resize(uint32_t width, uint32_t height)
{
    Stats.Width = width;
    Stats.Height = height;
}

void RenderBackend_NullRHI::RequestScreenshot(const wchar_t*)
{
    EngineLog::Warning("RenderCore", "NullRHI has no framebuffer; screenshot request was ignored");
}
