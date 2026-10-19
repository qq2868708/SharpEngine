#include "RenderBackend_OpenGL.h"

#include "../../../Logging/EngineLog.h"
#include "../../Public/RenderCore.h"

#include <Windows.h>
#include <GL/gl.h>
#include <RmlUi_Renderer_GL2.h>
#include "../RenderCapture.h"

namespace
{
    void DrawVertices(GLenum primitive, const RenderDrawList& list)
    {
        if (!list.Vertices || list.VertexCount == 0)
        {
            return;
        }

        glBegin(primitive);
        for (uint32_t index = 0; index < list.VertexCount; ++index)
        {
            const auto& vertex = list.Vertices[index];
            glColor4f(vertex.R, vertex.G, vertex.B, vertex.A);
            glVertex3f(vertex.X, vertex.Y, vertex.Z);
        }
        glEnd();
    }
} // namespace

struct RenderBackend_OpenGL::Surface
{
    struct UiRenderer : RenderInterface_GL2
    {
        HDC Dc = nullptr;
        HGLRC Context = nullptr;
        uint32_t DrawCalls = 0;
        void RenderGeometry(Rml::CompiledGeometryHandle handle, Rml::Vector2f translation, Rml::TextureHandle texture) override
        {
            ++DrawCalls;
            RenderInterface_GL2::RenderGeometry(handle, translation, texture);
        }
        void ReleaseTexture(Rml::TextureHandle texture) override
        {
            // RmlUi can release font resources for several RenderManagers in
            // one call. Texture names belong to their original GL context.
            const auto previousDc = wglGetCurrentDC();
            const auto previousContext = wglGetCurrentContext();
            if (previousContext != Context)
            {
                wglMakeCurrent(Dc, Context);
            }
            RenderInterface_GL2::ReleaseTexture(texture);
            if (previousContext != Context)
            {
                wglMakeCurrent(previousDc, previousContext);
            }
        }
    } Renderer;
    HWND Window = nullptr;
    HDC Dc = nullptr;
    HGLRC Context = nullptr;
    bool Presented = false;
    ~Surface()
    {
        if (Context)
        {
            if (wglGetCurrentContext() == Context)
            {
                wglMakeCurrent(nullptr, nullptr);
            }
            wglDeleteContext(Context);
        }
        if (Dc && Window)
        {
            ReleaseDC(Window, Dc);
        }
    }
};

RenderBackend_OpenGL::RenderBackend_OpenGL() = default;
RenderBackend_OpenGL::~RenderBackend_OpenGL() { Shutdown(); }

bool RenderBackend_OpenGL::Initialize(const RenderInitDesc* desc)
{
    if (!desc || !desc->WindowHandle || !desc->Width || !desc->Height)
    {
        return false;
    }
    State = std::make_unique<Surface>();
    State->Window = static_cast<HWND>(desc->WindowHandle);
    State->Dc = GetDC(State->Window);
    if (!State->Dc)
    {
        return false;
    }
    PIXELFORMATDESCRIPTOR format{};
    format.nSize = sizeof(format);
    format.nVersion = 1;
    format.dwFlags = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER;
    format.iPixelType = PFD_TYPE_RGBA;
    format.cColorBits = 32;
    format.cDepthBits = 24;
    format.cStencilBits = 8;
    const int pixelFormat = ChoosePixelFormat(State->Dc, &format);
    if (!pixelFormat || !SetPixelFormat(State->Dc, pixelFormat, &format))
    {
        return false;
    }
    State->Context = wglCreateContext(State->Dc);
    if (!State->Context || !MakeCurrent())
    {
        return false;
    }
    State->Renderer.Dc = State->Dc;
    State->Renderer.Context = State->Context;

    Width = desc->Width;
    Height = desc->Height;
    Initialized = true;
    Resize(Width, Height);
    Stats.HasFramebuffer = true;
    EngineLog::Info("RenderCore", "Graphics backend initialized: opengl");
    return true;
}

void RenderBackend_OpenGL::Shutdown()
{
    Initialized = false;
    Width = 0;
    Height = 0;
    State.reset();
}

void RenderBackend_OpenGL::BeginFrame()
{
    if (!Initialized || !MakeCurrent())
    {
        return;
    }
    ++Stats.FramesBegun;
    State->Presented = false;
    State->Renderer.DrawCalls = 0;
    State->Renderer.BeginFrame();
    State->Renderer.Clear();
}

void RenderBackend_OpenGL::SubmitFrame(const RenderFrame* frame)
{
    if (!Initialized || !frame || frame->ViewportWidth <= 0 || frame->ViewportHeight <= 0)
    {
        return;
    }

    const int viewportX = static_cast<int>(frame->ViewportX);
    const int viewportY = static_cast<int>(frame->ClientHeight - frame->ViewportY - frame->ViewportHeight);
    const int viewportWidth = static_cast<int>(frame->ViewportWidth);
    const int viewportHeight = static_cast<int>(frame->ViewportHeight);
    if (viewportWidth <= 0 || viewportHeight <= 0)
    {
        return;
    }
    ++Stats.FramesSubmitted;
    Stats.TriangleVertices += frame->Triangles.VertexCount;
    Stats.LineVertices += frame->Lines.VertexCount;

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
    if (!Initialized)
    {
        return;
    }
    State->Renderer.EndFrame();
    State->Presented = SwapBuffers(State->Dc) != FALSE;
}

void RenderBackend_OpenGL::Resize(uint32_t width, uint32_t height)
{
    Width = width;
    Height = height;
    Stats.Width = width;
    Stats.Height = height;
    if (State)
    {
        State->Renderer.SetViewport(static_cast<int>(width), static_cast<int>(height));
    }
}

void RenderBackend_OpenGL::RequestScreenshot(const wchar_t* path)
{
    std::vector<unsigned char> pixels;
    uint32_t width{}, height{};
    if (path && ReadPixels(pixels, width, height) && WriteRenderCapture(path, pixels, width, height))
    {
        return;
    }
    EngineLog::Error("RenderCore", "OpenGL screenshot failed");
}

Rml::RenderInterface* RenderBackend_OpenGL::GetUiRenderer() { return State ? &State->Renderer : nullptr; }
bool RenderBackend_OpenGL::MakeCurrent()
{
    return State && State->Dc && State->Context && wglMakeCurrent(State->Dc, State->Context) != FALSE;
}
uint32_t RenderBackend_OpenGL::GetUiDrawCalls() const { return State ? State->Renderer.DrawCalls : 0; }
bool RenderBackend_OpenGL::WasPresented() const { return State && State->Presented; }

bool RenderBackend_OpenGL::ReadPixels(std::vector<unsigned char>& pixels, uint32_t& width, uint32_t& height)
{
    if (!Initialized || !MakeCurrent() || !State->Presented)
    {
        return false;
    }
    width = Width;
    height = Height;
    pixels.resize(size_t(width) * height * 4);
    glReadBuffer(GL_FRONT);
    glReadPixels(0, 0, width, height, 0x80E1, GL_UNSIGNED_BYTE, pixels.data());
    return glGetError() == GL_NO_ERROR;
}
