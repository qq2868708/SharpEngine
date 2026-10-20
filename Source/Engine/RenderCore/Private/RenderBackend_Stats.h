#pragma once

#include "../Public/RenderCore.h"

struct RenderBackendStats
{
    uint64_t FramesBegun = 0;
    uint64_t FramesSubmitted = 0;
    uint64_t TriangleVertices = 0;
    uint64_t LineVertices = 0;
    uint32_t Width = 0;
    uint32_t Height = 0;
    bool HasFramebuffer = false;
    bool VSyncEnabled = false;
    bool ValidationEnabled = false;

    RenderStats Public() const
    {
        RenderStats Result{};
        Result.FramesBegun = FramesBegun;
        Result.FramesSubmitted = FramesSubmitted;
        Result.TriangleVertices = TriangleVertices;
        Result.LineVertices = LineVertices;
        Result.Width = Width;
        Result.Height = Height;
        Result.HasFramebuffer = static_cast<uint8_t>(HasFramebuffer ? 1 : 0);
        Result.VSyncEnabled = static_cast<uint8_t>(VSyncEnabled ? 1 : 0);
        Result.ValidationEnabled = static_cast<uint8_t>(
            ValidationEnabled ? 1 : 0);
        return Result;
    }
};
