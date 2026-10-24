#include "UI/RmlUiSourceUrl.h"
#include "UI/RmlUiResourcePath.h"
#include <RmlUi/Core.h>
#include <RmlUi/Core/URL.h>
#include <iostream>
#include <stdexcept>

class RecordingSystem final : public Rml::SystemInterface
{
public:
    bool LogMessage(Rml::Log::Type, const Rml::String&) override { return true; }
};

class LayoutRenderer final : public Rml::RenderInterface
{
public:
    Rml::CompiledGeometryHandle CompileGeometry(Rml::Span<const Rml::Vertex>, Rml::Span<const int>) override { return 1; }
    void RenderGeometry(Rml::CompiledGeometryHandle, Rml::Vector2f, Rml::TextureHandle) override
    {
    }
    void ReleaseGeometry(Rml::CompiledGeometryHandle) override
    {
    }
    Rml::TextureHandle LoadTexture(Rml::Vector2i&, const Rml::String&) override { return 0; }
    Rml::TextureHandle GenerateTexture(Rml::Span<const Rml::byte>, Rml::Vector2i) override { return 0; }
    void ReleaseTexture(Rml::TextureHandle) override
    {
    }
    void EnableScissorRegion(bool) override
    {
    }
    void SetScissorRegion(Rml::Rectanglei) override
    {
    }
};

class ClickCounter final : public Rml::EventListener
{
public:
    int Count{};
    void ProcessEvent(Rml::Event&) override
    {
        ++Count;
    }
};

int Run()
{
    RecordingSystem System;
    LayoutRenderer Renderer;
    bool NativeReady = false;
    Rml::SetSystemInterface(&System);
    int Assertions = 0;
    auto Check = [&](bool Condition, const char* Message)
    {
        if (!Condition)
        {
            std::cerr << "FAIL: " << Message << std::endl;
            if (NativeReady)
            {
                Rml::Shutdown();
                NativeReady = false;
            }
            throw std::runtime_error(Message);
        }
        ++Assertions;
    };
    const std::string Raw = R"(D:\SharpEngineProjects\UI Sample\Screen.sui)";
    Rml::URL RawUrl;
    Check(!RawUrl.SetURL(Raw), "Reproduce RmlUi rejecting a raw Windows drive as a malformed protocol");
    const auto Source = RmlUiSourceUrl::FromFilePath(Raw);
    Rml::URL SourceUrl(Source);
    Check(SourceUrl.GetFileName() == "Screen" && SourceUrl.GetExtension() == "sui",
        "The actual RmlUi URL constructor accepts the converted source and retains its identity");
    Rml::String Joined;
    System.JoinPath(Joined, Source, "Theme.sglobalcss");
    Check(Joined == "D:/SharpEngineProjects/UI Sample/Theme.sglobalcss",
        "Relative shared styles retain their original filesystem location");
    System.JoinPath(Joined, Source, "Images/icon.png");
    Check(Joined == "D:/SharpEngineProjects/UI Sample/Images/icon.png",
        "Relative image resources stay relative to the authoritative package directory");
    Check(RmlUiSourceUrl::FromFilePath(Source) == Source, "Conversion is idempotent");
    const std::string Resource = "sharpengine-resource://project/Resources/icon.tga";
    Check(RmlUiSourceUrl::FromFilePath(Resource) == Resource, "A real URI scheme is preserved");
    Rml::URL Relative(RmlUiSourceUrl::FromFilePath("Content/UI/Screen.sui"));
    Check(Relative.GetFileName() == "Screen", "Relative file sources remain supported");
    Check(RmlUiSourceUrl::FromFilePath("").empty(), "Empty source handling remains with the host");
    const std::filesystem::path EngineRoot = LR"(D:\Engine)";
    const std::filesystem::path ProjectRoot = LR"(E:\Game)";
    Check(RmlUiResourcePath::Resolve(EngineRoot, ProjectRoot,
        "sharpengine-resource://engine/Resources/Editor/Icons/texture.tga") == EngineRoot / L"Resources/Editor/Icons/texture.tga",
        "Editor and Game resolve the same engine resource URI");
    Check(RmlUiResourcePath::Resolve(EngineRoot, ProjectRoot,
        "sharpengine-resource://project/Resources/icon.tga") == ProjectRoot / L"Resources/icon.tga",
        "Project resources use the project root rather than the engine installation");
    Check(RmlUiResourcePath::Resolve(EngineRoot, ProjectRoot,
        "sharpengine-resource://project/Plugins/Test/Resources/icon.tga") == ProjectRoot / L"Plugins/Test/Resources/icon.tga",
        "Plugin resource mounts retain their existing ownership contract");
    Check(RmlUiResourcePath::Resolve(EngineRoot, ProjectRoot,
        "sharpengine-resource://project/Resources/../private.tga").empty() &&
        RmlUiResourcePath::Resolve(EngineRoot, ProjectRoot, "sharpengine-resource://project/Content/icon.tga").empty(),
        "Resource URI resolution rejects traversal and non-resource paths");
    Check(RmlUiResourcePath::Resolve(EngineRoot, ProjectRoot,
        "sharpengine-resource://unknown/Resources/icon.tga").empty(), "Unknown resource owners are rejected");
    SetEnvironmentVariableW(L"SHARPENGINE_PROJECT_MANIFEST", LR"(E:\Game\Sample.sharpproject)");
    Check(RmlUiResourcePath::ProjectRootFromEnvironment() == ProjectRoot,
        "The resolver sees the project selected by the native host's Win32 environment API");
    Rml::SetRenderInterface(&Renderer);
    Check(Rml::Initialise(), "Initialize the native layout engine");
    NativeReady = true;
    auto* Context = Rml::CreateContext("responsive-layout-validation", {1000, 800});
    Check(Context != nullptr, "Create an independent native layout context");
    auto* Document = Context->LoadDocumentFromMemory(R"(
        <rml><head><style>
        body { margin: 0; width: 100%; height: 100%; }
        div { display: block; }
        #viewport { position: absolute; left: 100px; top: 100px; width: 400px; height: 300px; overflow: hidden; }
        #canvas { position: relative; width: 1000px; height: 600px; transform-origin: 0px 0px; transform: scale(0.5); }
        #target { position: absolute; left: 600px; top: 400px; width: 100px; height: 40px; }
        #clipped { position: absolute; left: 900px; top: 400px; width: 100px; height: 40px; }
        #percent { position: absolute; left: 0px; top: 0px; width: 25%; height: 40px; }
        @media (max-width: 800px) { #percent { width: 50%; } }
        </style></head><body><div id="viewport"><div id="canvas"><button id="target" /><button id="clipped" /></div></div><div id="percent" /></body></rml>
    )");
    Check(Document != nullptr, "Load a transformed and clipped native document");
    Document->Show();
    Context->Update();
    Context->Render();
    auto* Target = Document->GetElementById("target");
    Check(Context->GetElementAtPoint({425, 310}) == Target,
        "A visible scaled control is hit through differently transformed clipping ancestors");
    Check(Context->GetElementAtPoint({575, 310}) != Document->GetElementById("clipped"),
        "A transformed control outside its clipping viewport remains unclickable");
    ClickCounter Clicks;
    Target->AddEventListener(Rml::EventId::Click, &Clicks);
    Context->ProcessMouseMove(425, 310, 0);
    Context->ProcessMouseButtonDown(0, 0);
    Context->ProcessMouseButtonUp(0, 0);
    Check(Clicks.Count == 1, "The real native pointer and bubbling route delivers the scaled control click once");
    auto* Percent = Document->GetElementById("percent");
    Context->SetDimensions({1280, 720});
    Context->Update();
    Check(Percent->GetBox().GetSize().x == 320, "Percentage width follows the context dimensions");
    Context->SetDimensions({2560, 1080});
    Context->Update();
    Check(Percent->GetBox().GetSize().x == 640, "The same percentage source adapts to an ultrawide viewport");
    Context->SetDimensions({640, 480});
    Context->Update();
    Check(Percent->GetBox().GetSize().x == 320, "The native media query evaluates against its own context");
    Target->RemoveEventListener(Rml::EventId::Click, &Clicks);
    Rml::RemoveContext("responsive-layout-validation");
    Rml::Shutdown();
    NativeReady = false;
    Rml::SetRenderInterface(nullptr);
    std::cout << "PASS: " << Assertions << " native RmlUi URL/resource/layout assertions\n";
    Rml::SetSystemInterface(nullptr);
    return 0;
}

int main()
{
    try
    {
        return Run();
    }
    catch (const std::exception& Error)
    {
        std::cerr << Error.what() << std::endl;
        return 1;
    }
}
