#include "RenderCore/Public/PreviewRenderer.h"

void RenderPreviewScene(float x, float y, float width, float height, float clientHeight,
    const UiSceneVertex* triangles, uint32_t triangleVertices,
    const UiSceneVertex* lines, uint32_t lineVertices)
{
    if (width <= 0 || height <= 0) return;
    const RenderFrame frame{
        x, y, width, height, clientHeight,
        {triangles, triangleVertices},
        {lines, lineVertices},
    };
    RC_BeginFrame();
    RC_SubmitFrame(&frame);
    RC_EndFrame();
}
