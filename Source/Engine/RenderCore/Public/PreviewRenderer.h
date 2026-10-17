#pragma once
#include <cstdint>
#include "RenderCore.h"

using UiSceneVertex = RenderVertex;

// Shared GL preview presentation used by Editor viewport and Game window.
void RenderPreviewScene(float x, float y, float width, float height, float clientHeight,
    const UiSceneVertex* triangles, uint32_t triangleVertices,
    const UiSceneVertex* lines, uint32_t lineVertices);
