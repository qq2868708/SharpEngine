#pragma once
#include <cstdint>

struct UiSceneVertex { float X, Y, Z, R, G, B, A; };

// Shared GL preview presentation used by Editor viewport and Game window.
void RenderPreviewScene(float x, float y, float width, float height, float clientHeight,
    const UiSceneVertex* triangles, uint32_t triangleVertices,
    const UiSceneVertex* lines, uint32_t lineVertices);
