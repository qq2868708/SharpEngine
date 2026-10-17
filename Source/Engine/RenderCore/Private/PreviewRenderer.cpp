#include <windows.h>
#include <GL/gl.h>
#include "RenderCore/Public/PreviewRenderer.h"

void RenderPreviewScene(float x, float y, float width, float height, float clientHeight,
    const UiSceneVertex* triangles, uint32_t triangleVertices,
    const UiSceneVertex* lines, uint32_t lineVertices)
{
    if (width <= 0 || height <= 0) return;
    glPushAttrib(GL_ALL_ATTRIB_BITS);
    glPushClientAttrib(GL_CLIENT_ALL_ATTRIB_BITS);
    glMatrixMode(GL_PROJECTION); glPushMatrix(); glLoadIdentity();
    glOrtho(0, width, height, 0, 0, -1);
    glMatrixMode(GL_MODELVIEW); glPushMatrix(); glLoadIdentity();
    glViewport(static_cast<int>(x), static_cast<int>(clientHeight-y-height), static_cast<int>(width), static_cast<int>(height));
    glEnable(GL_SCISSOR_TEST);
    glScissor(static_cast<int>(x), static_cast<int>(clientHeight-y-height), static_cast<int>(width), static_cast<int>(height));
    glDisable(GL_TEXTURE_2D); glDisable(GL_BLEND); glDisable(GL_STENCIL_TEST);
    glClearColor(0.055f, 0.065f, 0.08f, 1);
    glClear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
    glEnable(GL_DEPTH_TEST); glDepthFunc(GL_LEQUAL);
    glEnable(GL_POLYGON_OFFSET_FILL); glPolygonOffset(1, 1);
    glBegin(GL_TRIANGLES);
    for (uint32_t i=0; i<triangleVertices; ++i) {
        const auto& v=triangles[i]; glColor4f(v.R,v.G,v.B,v.A); glVertex3f(v.X,v.Y,v.Z);
    }
    glEnd(); glDisable(GL_POLYGON_OFFSET_FILL);
    glBegin(GL_LINES);
    for (uint32_t i=0; i<lineVertices; ++i) {
        const auto& v=lines[i]; glColor4f(v.R,v.G,v.B,v.A); glVertex3f(v.X,v.Y,v.Z);
    }
    glEnd(); glMatrixMode(GL_MODELVIEW); glPopMatrix();
    glMatrixMode(GL_PROJECTION); glPopMatrix();
    glPopClientAttrib(); glPopAttrib();
}
