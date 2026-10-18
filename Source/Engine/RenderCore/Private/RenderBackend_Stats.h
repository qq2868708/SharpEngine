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

    RenderStats Public() const
    {
        return {
            FramesBegun, FramesSubmitted, TriangleVertices, LineVertices,
            Width, Height, static_cast<uint8_t>(HasFramebuffer ? 1 : 0),
        };
    }
};
