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

int main()
{
    RecordingSystem System;
    Rml::SetSystemInterface(&System);
    int Assertions = 0;
    auto Check = [&](bool Condition, const char* Message)
    {
        if (!Condition)
        {
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
    std::cout << "PASS: " << Assertions << " native RmlUi URL/resource assertions\n";
    Rml::SetSystemInterface(nullptr);
    return 0;
}
