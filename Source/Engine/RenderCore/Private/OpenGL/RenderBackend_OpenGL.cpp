#include "RenderBackend_OpenGL.h"

#include "../../../Logging/EngineLog.h"
#include "../../Public/RenderCore.h"

#include <Windows.h>
#include <GL/gl.h>

namespace
{
    void DrawVertices(GLenum primitive, const RenderDrawList& list)
    {
        if (!list.Vertices || list.VertexCount == 0) return;

        glBegin(primitive);
        for (uint32_t index = 0; index < list.VertexCount; ++index)
        {
            const auto& vertex = list.Vertices[index];
            glColor4f(vertex.R, vertex.G, vertex.B, vertex.A);
            glVertex3f(vertex.X, vertex.Y, vertex.Z);
        }
        glEnd();
    }
}

bool RenderBackend_OpenGL::Initialize(const RenderInitDesc* desc)
{
    if (!desc || !wglGetCurrentContext())
    {
        EngineLog::Error("RenderCore", "OpenGL backend requires an active graphics context");
        return false;
    }

    Width = desc->Width;
    Height = desc->Height;
    Initialized = true;
    return true;
}

void RenderBackend_OpenGL::Shutdown()
{
    Initialized = false;
    Width = 0;
    Height = 0;
}

void RenderBackend_OpenGL::BeginFrame()
{
    // The UI shell owns the frame clear. The scene pass only saves and
    // restores the state it changes, so it can be composed into RmlUi.
}

void RenderBackend_OpenGL::SubmitFrame(const RenderFrame* frame)
{
    if (!Initialized || !frame || frame->ViewportWidth <= 0 ||
        frame->ViewportHeight <= 0)
    {
        return;
    }

    const int viewportX = static_cast<int>(frame->ViewportX);
    const int viewportY = static_cast<int>(
        frame->ClientHeight - frame->ViewportY - frame->ViewportHeight);
    const int viewportWidth = static_cast<int>(frame->ViewportWidth);
    const int viewportHeight = static_cast<int>(frame->ViewportHeight);
    if (viewportWidth <= 0 || viewportHeight <= 0) return;

    glPushAttrib(GL_ALL_ATTRIB_BITS);
    glPushClientAttrib(GL_CLIENT_ALL_ATTRIB_BITS);
    glMatrixMode(GL_PROJECTION);
    glPushMatrix();
    glLoadIdentity();
    glOrtho(0, frame->ViewportWidth, frame->ViewportHeight, 0, 0, -1);
    glMatrixMode(GL_MODELVIEW);
    glPushMatrix();
    glLoadIdentity();

    glViewport(viewportX, viewportY, viewportWidth, viewportHeight);
    glEnable(GL_SCISSOR_TEST);
    glScissor(viewportX, viewportY, viewportWidth, viewportHeight);
    glDisable(GL_TEXTURE_2D);
    glDisable(GL_BLEND);
    glDisable(GL_STENCIL_TEST);
    glClearColor(0.055f, 0.065f, 0.08f, 1.0f);
    glClear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
    glEnable(GL_DEPTH_TEST);
    glDepthFunc(GL_LEQUAL);
    glEnable(GL_POLYGON_OFFSET_FILL);
    glPolygonOffset(1.0f, 1.0f);

    DrawVertices(GL_TRIANGLES, frame->Triangles);
    glDisable(GL_POLYGON_OFFSET_FILL);
    DrawVertices(GL_LINES, frame->Lines);

    glMatrixMode(GL_MODELVIEW);
    glPopMatrix();
    glMatrixMode(GL_PROJECTION);
    glPopMatrix();
    glPopClientAttrib();
    glPopAttrib();
}

void RenderBackend_OpenGL::EndFrame()
{
}

void RenderBackend_OpenGL::Resize(uint32_t width, uint32_t height)
{
    Width = width;
    Height = height;
}

void RenderBackend_OpenGL::RequestScreenshot(const wchar_t* path)
{
    // The Editor owns its OpenGL presentation and captures through RmlUiShell.
    // Game uses the Vulkan backend, so there is no OpenGL readback path here.
    (void)path;
}
