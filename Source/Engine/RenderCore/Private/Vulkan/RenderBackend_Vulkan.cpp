#include "RenderBackend_Vulkan.h"

#include "../../../Logging/EngineLog.h"
#include "../../Public/RenderCore.h"

#ifdef SHARPENGINE_GAME

#include <RmlUi/Core/Context.h>
#include <RmlUi/Core/Core.h>
#include <RmlUi/Core/Element.h>
#include <RmlUi/Core/ElementDocument.h>
#include <RmlUi/Core/EventListener.h>
#include <RmlUi/Core/FileInterface.h>
#include <RmlUi/Core/Vertex.h>
#include <RmlUi_Platform_Win32.h>
#include <RmlUi_Renderer_VK.h>

#include <windows.h>
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

namespace
{
    HWND GSurfaceWindow = nullptr;

    static std::filesystem::path GameRoot()
    {
        const wchar_t* root = _wgetenv(L"SHARPENGINE_ROOT");
        return root ? std::filesystem::path(root) : std::filesystem::current_path();
    }

    class GameFileInterface final : public Rml::FileInterface
    {
    public:
        explicit GameFileInterface(std::filesystem::path root)
            : Root(std::move(root))
        {
        }

        Rml::FileHandle Open(const Rml::String& path) override
        {
            std::filesystem::path requested = std::filesystem::u8path(path);
            if (requested.is_relative()) requested = Root / requested;
            return reinterpret_cast<Rml::FileHandle>(_wfopen(requested.c_str(), L"rb"));
        }

        void Close(Rml::FileHandle file) override
        {
            if (file) fclose(reinterpret_cast<FILE*>(file));
        }

        size_t Read(void* buffer, size_t size, Rml::FileHandle file) override
        {
            return file ? fread(buffer, 1, size, reinterpret_cast<FILE*>(file)) : 0;
        }

        bool Seek(Rml::FileHandle file, long offset, int origin) override
        {
            return file && fseek(reinterpret_cast<FILE*>(file), offset, origin) == 0;
        }

        size_t Tell(Rml::FileHandle file) override
        {
            return file ? static_cast<size_t>(ftell(reinterpret_cast<FILE*>(file))) : 0;
        }

    private:
        std::filesystem::path Root;
    };

    class GameUiEvents final : public Rml::EventListener
    {
    public:
        explicit GameUiEvents(Rml::ElementDocument** document)
            : Document(document)
        {
        }

        void ProcessEvent(Rml::Event& event) override
        {
            if (event.GetId() != Rml::EventId::Click || !Document || !*Document)
                return;

            const auto* target = event.GetTargetElement();
            const std::string id = target ? target->GetId().c_str() : std::string{};
            if (id == "continueButton") SetStatus("RUNNING");
            else if (id == "settingsButton") SetStatus("SETTINGS");
        }

    private:
        void SetStatus(const char* value)
        {
            if (auto* status = (*Document)->GetElementById("status"))
                status->SetInnerRML(value);
        }

        Rml::ElementDocument** Document = nullptr;
    };

    bool CreateGameSurface(VkInstance instance, VkSurfaceKHR* outSurface)
    {
        if (!GSurfaceWindow || !outSurface) return false;
        VkWin32SurfaceCreateInfoKHR info{};
        info.sType = VK_STRUCTURE_TYPE_WIN32_SURFACE_CREATE_INFO_KHR;
        info.hinstance = GetModuleHandleW(nullptr);
        info.hwnd = GSurfaceWindow;
        return vkCreateWin32SurfaceKHR(instance, &info, nullptr, outSurface) == VK_SUCCESS;
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
        vertex.position = {x, y};
        vertex.colour = {
            static_cast<unsigned char>(std::lround(ToByte(source.R) * alphaScale)),
            static_cast<unsigned char>(std::lround(ToByte(source.G) * alphaScale)),
            static_cast<unsigned char>(std::lround(ToByte(source.B) * alphaScale)),
            alpha};
        vertex.tex_coord = {0.0f, 0.0f};
        return vertex;
    }

    static void AppendSceneGeometry(
        const RenderDrawList& list,
        std::vector<Rml::Vertex>& vertices,
        std::vector<int>& indices)
    {
        if (!list.Vertices || list.VertexCount < 3) return;
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

    static void AppendLineGeometry(
        const RenderDrawList& list,
        std::vector<Rml::Vertex>& vertices,
        std::vector<int>& indices)
    {
        if (!list.Vertices || list.VertexCount < 2) return;
        constexpr float lineWidth = 1.5f;
        for (uint32_t i = 0; i + 1 < list.VertexCount; i += 2)
        {
            const auto& a = list.Vertices[i];
            const auto& b = list.Vertices[i + 1];
            const float dx = b.X - a.X;
            const float dy = b.Y - a.Y;
            const float length = std::sqrt(dx * dx + dy * dy);
            if (length < 0.001f) continue;
            const float nx = -dy / length * lineWidth * 0.5f;
            const float ny = dx / length * lineWidth * 0.5f;
            const int base = static_cast<int>(vertices.size());
            vertices.push_back(MakeVertex(a, a.X + nx, a.Y + ny));
            vertices.push_back(MakeVertex(a, a.X - nx, a.Y - ny));
            vertices.push_back(MakeVertex(b, b.X - nx, b.Y - ny));
            vertices.push_back(MakeVertex(b, b.X + nx, b.Y + ny));
            indices.insert(indices.end(), {base, base + 1, base + 2,
                                            base, base + 2, base + 3});
        }
    }

    static bool WriteWindowCapture(HWND window, const std::filesystem::path& path)
    {
        RECT client{};
        if (!GetClientRect(window, &client) || client.right <= 0 || client.bottom <= 0)
            return false;
        const int width = client.right;
        const int height = client.bottom;
        HDC windowDc = GetDC(window);
        HDC memoryDc = CreateCompatibleDC(windowDc);
        HBITMAP bitmap = CreateCompatibleBitmap(windowDc, width, height);
        HGDIOBJ previous = SelectObject(memoryDc, bitmap);
        BOOL captured = PrintWindow(window, memoryDc, 0);
        if (!captured)
            captured = BitBlt(memoryDc, 0, 0, width, height, windowDc, 0, 0, SRCCOPY | CAPTUREBLT);

        BITMAPINFO info{};
        info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        info.bmiHeader.biWidth = width;
        info.bmiHeader.biHeight = -height;
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        info.bmiHeader.biCompression = BI_RGB;
        std::vector<unsigned char> pixels(static_cast<size_t>(width) * height * 4);
        if (captured)
            captured = GetDIBits(memoryDc, bitmap, 0, height, pixels.data(), &info, DIB_RGB_COLORS);

        SelectObject(memoryDc, previous);
        DeleteObject(bitmap);
        DeleteDC(memoryDc);
        ReleaseDC(window, windowDc);
        if (!captured) return false;

        BITMAPFILEHEADER file{};
        file.bfType = 0x4d42;
        file.bfOffBits = sizeof(BITMAPFILEHEADER) + sizeof(BITMAPINFOHEADER);
        file.bfSize = file.bfOffBits + static_cast<DWORD>(pixels.size());
        info.bmiHeader.biHeight = height;
        std::ofstream output(path, std::ios::binary);
        output.write(reinterpret_cast<const char*>(&file), sizeof(file));
        output.write(reinterpret_cast<const char*>(&info.bmiHeader), sizeof(info.bmiHeader));
        output.write(reinterpret_cast<const char*>(pixels.data()), pixels.size());
        return output.good();
    }
}

struct RenderBackend_Vulkan::GameState
{
    explicit GameState(HWND window, uint32_t width, uint32_t height)
        : Window(window), FileSystem(GameRoot()), Events(&Document)
    {
        GSurfaceWindow = window;
        Rml::Vector<const char*> extensions;
        extensions.push_back(VK_KHR_SURFACE_EXTENSION_NAME);
        extensions.push_back(VK_KHR_WIN32_SURFACE_EXTENSION_NAME);
        if (!Renderer.Initialize(std::move(extensions), CreateGameSurface)) return;

        System.SetWindow(window);
        Rml::SetSystemInterface(&System);
        Rml::SetRenderInterface(&Renderer);
        Rml::SetFileInterface(&FileSystem);
        Rml::SetTextInputHandler(&TextInput);
        if (!Rml::Initialise()) return;
        RmlReady = true;
        const bool fontLoaded = Rml::LoadFontFace("C:/Windows/Fonts/segoeui.ttf");
        EngineLog::Info("RmlUi", std::string("Game Vulkan font loaded=") + (fontLoaded ? "true" : "false"));
        Renderer.SetViewport(static_cast<int>(width), static_cast<int>(height));
        Context = Rml::CreateContext("game", {static_cast<int>(width), static_cast<int>(height)}, &Renderer);
        if (!Context) return;

        const std::filesystem::path documentPath = FileSystemRoot() / "Content" / "UI" / "Main.rml";
        Document = Context->LoadDocument(documentPath.generic_string());
        if (!Document) return;
        Document->AddEventListener(Rml::EventId::Click, &Events);
        if (auto* resolution = Document->GetElementById("resolution"))
            resolution->SetInnerRML(std::to_string(width) + " x " + std::to_string(height));
        Document->Show();
        Context->Update();
        Ready = true;
        EngineLog::Info("RmlUi", "Game Vulkan UI initialized document=" + documentPath.generic_string());
    }

    ~GameState() { Shutdown(); }

    std::filesystem::path FileSystemRoot() const
    {
        return GameRoot();
    }

    void Shutdown()
    {
        if (RmlReady)
        {
            Context = nullptr;
            Rml::Shutdown();
            RmlReady = false;
        }
        Rml::SetTextInputHandler(nullptr);
        Renderer.Shutdown();
        GSurfaceWindow = nullptr;
        Ready = false;
    }

    bool HandleMessage(uint32_t message, uintptr_t wParam, intptr_t lParam)
    {
        if (!Ready || !Context) return false;
        if (message == WM_SIZE)
        {
            const uint32_t width = LOWORD(lParam);
            const uint32_t height = HIWORD(lParam);
            if (width != 0 && height != 0)
            {
                Renderer.SetViewport(static_cast<int>(width), static_cast<int>(height));
                Context->SetDimensions({static_cast<int>(width), static_cast<int>(height)});
            }
            return false;
        }
        return !RmlWin32::WindowProcedure(Context, TextInput, Window,
                                           static_cast<UINT>(message),
                                           static_cast<WPARAM>(wParam),
                                           static_cast<LPARAM>(lParam));
    }

    bool IsFocused() const { return Ready && Context && Context->GetFocusElement() != nullptr; }

    HWND Window = nullptr;
    RenderInterface_VK Renderer;
    SystemInterface_Win32 System;
    TextInputMethodEditor_Win32 TextInput;
    GameFileInterface FileSystem;
    Rml::Context* Context = nullptr;
    Rml::ElementDocument* Document = nullptr;
    GameUiEvents Events;
    bool RmlReady = false;
    bool Ready = false;
};

#endif

RenderBackend_Vulkan::RenderBackend_Vulkan() = default;
RenderBackend_Vulkan::~RenderBackend_Vulkan() { Shutdown(); }

bool RenderBackend_Vulkan::Initialize(const RenderInitDesc* desc)
{
#ifdef SHARPENGINE_GAME
    if (!desc || !desc->WindowHandle || desc->Width == 0 || desc->Height == 0) return false;
    State = std::make_unique<GameState>(reinterpret_cast<HWND>(desc->WindowHandle), desc->Width, desc->Height);
    if (!State->Ready)
    {
        State.reset();
        EngineLog::Error("RenderCore", "Vulkan backend failed to initialize Game UI");
        return false;
    }
    return true;
#else
    (void)desc;
    return true;
#endif
}

void RenderBackend_Vulkan::Shutdown()
{
#ifdef SHARPENGINE_GAME
    State.reset();
#endif
}

void RenderBackend_Vulkan::BeginFrame()
{
#ifdef SHARPENGINE_GAME
    if (State && State->Ready) State->Renderer.BeginFrame();
#endif
}

void RenderBackend_Vulkan::SubmitFrame(const RenderFrame* frame)
{
#ifdef SHARPENGINE_GAME
    if (!State || !State->Ready || !frame || frame->ViewportWidth <= 0 || frame->ViewportHeight <= 0) return;
    std::vector<Rml::Vertex> vertices;
    std::vector<int> indices;
    AppendSceneGeometry(frame->Triangles, vertices, indices);
    AppendLineGeometry(frame->Lines, vertices, indices);
    if (vertices.empty() || indices.empty()) return;
    const auto geometry = State->Renderer.CompileGeometry({vertices.data(), vertices.size()}, {indices.data(), indices.size()});
    if (!geometry) return;
    State->Renderer.RenderGeometry(geometry, {}, {});
    State->Renderer.ReleaseGeometry(geometry);
#else
    (void)frame;
#endif
}

void RenderBackend_Vulkan::EndFrame()
{
#ifdef SHARPENGINE_GAME
    if (State && State->Ready)
    {
        State->Context->Update();
        State->Context->Render();
        State->Renderer.EndFrame();
    }
#endif
}

void RenderBackend_Vulkan::Resize(uint32_t width, uint32_t height)
{
#ifdef SHARPENGINE_GAME
    if (State && State->Ready && width > 0 && height > 0)
    {
        State->Renderer.SetViewport(static_cast<int>(width), static_cast<int>(height));
        State->Context->SetDimensions({static_cast<int>(width), static_cast<int>(height)});
    }
#else
    (void)width; (void)height;
#endif
}

void RenderBackend_Vulkan::RequestScreenshot(const wchar_t* path)
{
#ifdef SHARPENGINE_GAME
    if (State && State->Ready && path)
    {
        const std::string nativePath = std::filesystem::path(path).string();
        State->Renderer.CaptureScreenshot(nativePath.c_str());
    }
#else
    (void)path;
#endif
}

bool RenderBackend_Vulkan::HandleWindowMessage(void* window, uint32_t message,
                                               uintptr_t wParam, intptr_t lParam)
{
#ifdef SHARPENGINE_GAME
    if (!State || !State->Ready || reinterpret_cast<HWND>(window) != State->Window) return false;
    return State->HandleMessage(message, wParam, lParam);
#else
    (void)window; (void)message; (void)wParam; (void)lParam;
    return false;
#endif
}

bool RenderBackend_Vulkan::IsUiFocused() const
{
#ifdef SHARPENGINE_GAME
    return State && State->IsFocused();
#else
    return false;
#endif
}
