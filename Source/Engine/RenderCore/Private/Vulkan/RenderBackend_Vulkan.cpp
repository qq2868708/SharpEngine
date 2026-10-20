#include "RenderBackend_Vulkan.h"
#include "GpuSceneRenderer_Vulkan.h"
#include "../../../Logging/EngineLog.h"
#include "../RenderCapture.h"
#include <RmlUi_Renderer_VK.h>
#include <algorithm>
#include <cmath>
#include <filesystem>
#include <fstream>

namespace
{
    // The renderer invokes this synchronously during surface initialization.
    thread_local HWND GSurfaceWindow = nullptr;
    bool CreateSurface(VkInstance instance, VkSurfaceKHR* surface)
    {
        VkWin32SurfaceCreateInfoKHR info{};
        info.sType = VK_STRUCTURE_TYPE_WIN32_SURFACE_CREATE_INFO_KHR;
        info.hinstance = GetModuleHandleW(nullptr);
        info.hwnd = GSurfaceWindow;
        return vkCreateWin32SurfaceKHR(instance, &info, nullptr, surface) == VK_SUCCESS;
    }
    static unsigned char ToByte(float value)
    {
        const float clamped = (std::max)(0.0f, (std::min)(1.0f, value));
        return static_cast<unsigned char>(std::lround(clamped * 255.0f));
    }

    static Rml::Vertex MakeVertex(const RenderVertex& source, float x, float y)
    {
        const unsigned char alpha = ToByte(source.A);
        const float alphaScale = static_cast<float>(alpha) / 255.0f;
        Rml::Vertex vertex{};
        vertex.position = Rml::Vector2f(x, y);
        vertex.colour =
        {
            static_cast<unsigned char>(std::lround(ToByte(source.R) * alphaScale)),
            static_cast<unsigned char>(std::lround(ToByte(source.G) * alphaScale)),
            static_cast<unsigned char>(std::lround(ToByte(source.B) * alphaScale)),
            alpha
        };
        vertex.tex_coord = Rml::Vector2f(source.Z, 0.0f);
        return vertex;
    }

    static void AppendSceneGeometry(const RenderDrawList& list, std::vector<Rml::Vertex>& vertices, std::vector<int>& indices)
    {
        if (!list.Vertices || list.VertexCount < 3)
        {
            return;
        }
        const int base = static_cast<int>(vertices.size());
        vertices.reserve(vertices.size() + list.VertexCount);
        for (uint32_t i = 0; i < list.VertexCount; ++i)
        {
            const auto& source = list.Vertices[i];
            vertices.push_back(MakeVertex(source, source.X, source.Y));
        }
        indices.reserve(indices.size() + (list.VertexCount / 3) * 3);
        for (uint32_t i = 0; i + 2 < list.VertexCount; i += 3)
        {
            indices.push_back(base + static_cast<int>(i));
            indices.push_back(base + static_cast<int>(i + 1));
            indices.push_back(base + static_cast<int>(i + 2));
        }
    }

    static void AppendLineGeometry(const RenderDrawList& list, std::vector<Rml::Vertex>& vertices, std::vector<int>& indices)
    {
        if (!list.Vertices || list.VertexCount < 2)
        {
            return;
        }
        constexpr float lineWidth = 1.5f;
        for (uint32_t i = 0; i + 1 < list.VertexCount; i += 2)
        {
            const auto& a = list.Vertices[i];
            const auto& b = list.Vertices[i + 1];
            const float dx = b.X - a.X;
            const float dy = b.Y - a.Y;
            const float length = std::sqrt(dx * dx + dy * dy);
            if (length < 0.001f)
            {
                continue;
            }
            const float nx = -dy / length * lineWidth * 0.5f;
            const float ny = dx / length * lineWidth * 0.5f;
            const int base = static_cast<int>(vertices.size());
            vertices.push_back(MakeVertex(a, a.X + nx, a.Y + ny));
            vertices.push_back(MakeVertex(a, a.X - nx, a.Y - ny));
            vertices.push_back(MakeVertex(b, b.X - nx, b.Y - ny));
            vertices.push_back(MakeVertex(b, b.X + nx, b.Y + ny));
            indices.insert(indices.end(),
            {
                base, base + 1, base + 2, base, base + 2, base + 3
            });
        }
    }

} // namespace

struct RenderBackend_Vulkan::Surface
{
    struct UiRenderer : RenderInterface_VK
    {
        explicit UiRenderer(RmlUiVulkanDeviceContext& DeviceContext) :
            RenderInterface_VK(DeviceContext)
        {
        }
        uint32_t DrawCalls = 0;
        void RenderGeometry(Rml::CompiledGeometryHandle handle, Rml::Vector2f translation, Rml::TextureHandle texture) override
        {
            ++DrawCalls;
            RenderInterface_VK::RenderGeometry(handle, translation, texture);
        }
    };
    RmlUiVulkanDeviceContext DeviceContext;
    UiRenderer Renderer;
    GpuSceneRenderer_Vulkan SceneRenderer;
    bool Ready = false;
    bool Presented = false;
    bool GpuSceneRendered = false;
    Surface() : Renderer(DeviceContext) {}
    ~Surface()
    {
        if (Ready)
        {
            Renderer.Activate();
            SceneRenderer.Shutdown();
            Renderer.Shutdown();
        }
    }
};

RenderBackend_Vulkan::RenderBackend_Vulkan() = default;
RenderBackend_Vulkan::~RenderBackend_Vulkan() { Shutdown(); }

bool RenderBackend_Vulkan::Initialize(const RenderInitDesc* desc)
{
    if (!desc || !desc->WindowHandle || !desc->Width || !desc->Height)
    {
        return false;
    }
#ifndef RMLUI_VK_DEBUG
    if (desc->EnableValidation)
    {
        EngineLog::Error("RenderCore",
            "Vulkan validation is only available in Debug builds");
        return false;
    }
#endif
    State = std::make_unique<Surface>();
    GSurfaceWindow = static_cast<HWND>(desc->WindowHandle);
    const Rml::Vector<const char*> extensions
    {
        VK_KHR_SURFACE_EXTENSION_NAME, VK_KHR_WIN32_SURFACE_EXTENSION_NAME
    };
    const bool ready = State->Renderer.Initialize(extensions, CreateSurface,
        desc->EnableVSync, desc->EnableValidation);
    GSurfaceWindow = nullptr;
    if (!ready)
    {
        return false;
    }
    State->Ready = true;
    Resize(desc->Width, desc->Height);
    Stats.HasFramebuffer = true;
    Stats.VSyncEnabled = State->Renderer.IsVSyncEnabled();
    Stats.ValidationEnabled = State->Renderer.IsValidationEnabled();
    if (!desc->EnableVSync && Stats.VSyncEnabled)
    {
        EngineLog::Error("RenderCore",
            "Vulkan cannot disable VSync: immediate/mailbox present is unavailable");
        return false;
    }
    if (desc->EnableValidation && !Stats.ValidationEnabled)
    {
        EngineLog::Error("RenderCore",
            "Vulkan validation layer is unavailable");
        return false;
    }
    EngineLog::Info("RenderCore", std::string(
        "Graphics backend initialized: vulkan; vsync=") +
        (Stats.VSyncEnabled ? "on" : "off") + "; validation=" +
        (Stats.ValidationEnabled ? "on" : "off"));
    return State->Renderer.IsSwapchainValid();
}

void RenderBackend_Vulkan::Shutdown() { State.reset(); }
bool RenderBackend_Vulkan::MakeCurrent() { return State && State->Ready && State->Renderer.Activate(); }
Rml::RenderInterface* RenderBackend_Vulkan::GetUiRenderer() { return State ? &State->Renderer : nullptr; }
uint32_t RenderBackend_Vulkan::GetUiDrawCalls() const { return State ? State->Renderer.DrawCalls : 0; }
bool RenderBackend_Vulkan::WasPresented() const { return State && State->Presented; }
void RenderBackend_Vulkan::RequestReadback()
{
    if (State && State->Ready && MakeCurrent())
    {
        State->Renderer.RequestReadback();
    }
}

void RenderBackend_Vulkan::BeginFrame()
{
    if (!MakeCurrent())
    {
        return;
    }
    ++Stats.FramesBegun;
    State->Presented = false;
    State->GpuSceneRendered = false;
    State->Renderer.DrawCalls = 0;
    State->Renderer.BeginFrame();
}

void RenderBackend_Vulkan::SubmitFrame(const RenderFrame* frame)
{
    if (!State || !State->Ready || !frame || frame->ViewportWidth <= 0 || frame->ViewportHeight <= 0)
    {
        return;
    }
    ++Stats.FramesSubmitted;
    Stats.TriangleVertices += frame->Triangles.VertexCount;
    Stats.LineVertices += frame->Lines.VertexCount;
    std::vector<Rml::Vertex> vertices;
    std::vector<int> indices;
    const RenderVertex background[] =
    {
        {
            0, 0, 1, .055f, .065f, .08f, 1
        },
        {
            frame->ViewportWidth, 0, 1, .055f, .065f, .08f, 1
        },
        {
            frame->ViewportWidth, frame->ViewportHeight, 1, .055f, .065f, .08f, 1
        },
        {
            0, 0, 1, .055f, .065f, .08f, 1
        },
        {
            frame->ViewportWidth, frame->ViewportHeight, 1, .055f, .065f, .08f, 1
        },
        {
            0, frame->ViewportHeight, 1, .055f, .065f, .08f, 1
        },
    };
    const RenderDrawList backgroundList
    {
        background, 6
    };
    if (!State->GpuSceneRendered || frame->Triangles.VertexCount > 0)
    {
        AppendSceneGeometry(backgroundList, vertices, indices);
    }
    // The preview vertices are local to the scene viewport. UI and viewport
    // share this surface without owning each other's frame boundaries.
    AppendSceneGeometry(frame->Triangles, vertices, indices);
    AppendLineGeometry(frame->Lines, vertices, indices);
    if (vertices.empty() || indices.empty())
    {
        return;
    }
    auto& renderer = State->Renderer;
    const auto geometry = renderer.CompileGeometry(Rml::Span<const Rml::Vertex>(vertices.data(), vertices.size()),
        Rml::Span<const int>(indices.data(), indices.size()));
    if (!geometry)
    {
        return;
    }
    RmlUiVulkanFrameContext context{};
    const bool hasContext = renderer.GetFrameContext(context) &&
        context.command_buffer;
    if (hasContext)
    {
        const VkRect2D sceneScissor
        {
            {
                static_cast<int32_t>(frame->ViewportX),
                static_cast<int32_t>(frame->ViewportY)
            },
            {
                static_cast<uint32_t>(frame->ViewportWidth),
                static_cast<uint32_t>(frame->ViewportHeight)
            }
        };
        vkCmdSetViewport(context.command_buffer, 0, 1, &context.viewport);
        vkCmdSetScissor(context.command_buffer, 0, 1, &sceneScissor);
    }
    renderer.RenderGeometryWithDepth(geometry, Rml::Vector2f(frame->ViewportX, frame->ViewportY));
    if (hasContext)
    {
        vkCmdSetViewport(context.command_buffer, 0, 1, &context.viewport);
        vkCmdSetScissor(context.command_buffer, 0, 1, &context.scissor);
    }
    renderer.ReleaseGeometry(geometry);
}

bool RenderBackend_Vulkan::EnsureGpuMesh(const GpuMeshUpload* Upload)
{
    if (!State || !State->Ready || !MakeCurrent())
    {
        return false;
    }
    RmlUiVulkanFrameContext Context{};
    return State->Renderer.GetFrameContext(Context) &&
        State->SceneRenderer.EnsureMesh(Context, Upload);
}

bool RenderBackend_Vulkan::SubmitGpuScene(const GpuSceneFrame* Frame)
{
    if (!State || !State->Ready || !MakeCurrent())
    {
        return false;
    }
    RmlUiVulkanFrameContext Context{};
    State->GpuSceneRendered = State->Renderer.GetFrameContext(Context) &&
        State->SceneRenderer.Render(Context, Frame);
    return State->GpuSceneRendered;
}

bool RenderBackend_Vulkan::SupportsGpuScene() const
{
    return State && State->Ready;
}

void RenderBackend_Vulkan::EndFrame()
{
    if (!State || !State->Ready)
    {
        return;
    }
    State->Renderer.EndFrame();
    State->Presented = State->Renderer.IsSwapchainValid();
}

void RenderBackend_Vulkan::Resize(uint32_t width, uint32_t height)
{
    if (!width || !height || !MakeCurrent())
    {
        return;
    }
    if (Stats.Width == width && Stats.Height == height)
    {
        return;
    }
    State->Renderer.SetViewport(static_cast<int>(width), static_cast<int>(height));
    Stats.Width = width;
    Stats.Height = height;
}

bool RenderBackend_Vulkan::ReadPixels(std::vector<unsigned char>& pixels, uint32_t& width, uint32_t& height)
{
    if (!MakeCurrent() || !State->Presented)
    {
        return false;
    }
    int w{}, h{};
    if (!State->Renderer.ReadPixels(pixels, w, h))
    {
        return false;
    }
    width = static_cast<uint32_t>(w);
    height = static_cast<uint32_t>(h);
    return true;
}

void RenderBackend_Vulkan::RequestScreenshot(const wchar_t* path)
{
    std::vector<unsigned char> pixels;
    uint32_t width{}, height{};
    if (path && ReadPixels(pixels, width, height) && WriteRenderCapture(path, pixels, width, height))
    {
        return;
    }
    EngineLog::Error("RenderCore", "Vulkan screenshot failed");
}
