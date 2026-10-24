#include <windows.h>
#include <chrono>
#include <cstring>
#include <filesystem>
#include <iostream>
#include <shobjidl.h>
#include <string>
#include <string_view>
#include <thread>
#include "Platform/Platform.h"
#include "Platform/Windows/StartupSplash.h"
#include "ManagedHost.h"
#include "Logging/EngineLog.h"
#include "UI/RmlUiShell.h"
#include "RenderCore/Public/RenderOptions.h"

namespace
{
    RmlUiShell* GUi{};
    ManagedHost* GManaged{};
    StartupSplash* GSplash{};
    void __cdecl ReportStartupProgress(int32_t Percent, const char* Module, const char* Detail)
    {
        if (GSplash)
        {
            GSplash->Report(Percent, Module, Detail);
        }
    }
    void __cdecl SetWindowTitleUtf8(void* window, const char* text)
    {
        if (!window || !text) return;
        const int length = MultiByteToWideChar(CP_UTF8, 0, text, -1, nullptr, 0);
        std::wstring wide(length > 0 ? length : 1, L'\0');
        MultiByteToWideChar(CP_UTF8, 0, text, -1, wide.data(), length);
        SetWindowTextW(static_cast<HWND>(window), wide.c_str());
    }
    std::wstring Utf8ToWide(const char* text)
    {
        if (!text) return {};
        const int length=MultiByteToWideChar(CP_UTF8,0,text,-1,nullptr,0);
        if (length<=0) return {};
        std::wstring result(static_cast<size_t>(length),L'\0');
        MultiByteToWideChar(CP_UTF8,0,text,-1,result.data(),length);
        result.resize(static_cast<size_t>(length-1));
        return result;
    }
    std::string WideToUtf8(const wchar_t* text)
    {
        if (!text) return {};
        const int length=WideCharToMultiByte(CP_UTF8,0,text,-1,nullptr,0,nullptr,nullptr);
        if (length<=0) return {};
        std::string result(static_cast<size_t>(length),'\0');
        WideCharToMultiByte(CP_UTF8,0,text,-1,result.data(),length,nullptr,nullptr);
        result.resize(static_cast<size_t>(length-1));
        return result;
    }
    uint8_t __cdecl ChooseWorldSavePath(void* ownerWindow, const char* contentRoot,
                                        char* result, uint32_t capacity)
    {
        if (!ownerWindow || !contentRoot || !result || capacity==0) return 0;
        result[0]='\0';
        IFileSaveDialog* dialog=nullptr;
        if (FAILED(CoCreateInstance(CLSID_FileSaveDialog,nullptr,CLSCTX_INPROC_SERVER,
                                    IID_PPV_ARGS(&dialog)))) return 0;
        DWORD options{};
        dialog->GetOptions(&options);
        dialog->SetOptions(options|FOS_FORCEFILESYSTEM|FOS_PATHMUSTEXIST|
                           FOS_NOCHANGEDIR|FOS_OVERWRITEPROMPT);
        dialog->SetTitle(L"Save SharpEngine World");
        dialog->SetOkButtonLabel(L"Save World");
        dialog->SetDefaultExtension(L"sworld");
        dialog->SetFileName(L"Main.sworld");
        const COMDLG_FILTERSPEC filter{L"SharpEngine World",L"*.sworld"};
        dialog->SetFileTypes(1,&filter);
        IShellItem* folder=nullptr;
        const auto root=Utf8ToWide(contentRoot);
        if (!root.empty() && SUCCEEDED(SHCreateItemFromParsingName(root.c_str(),nullptr,
                                                IID_PPV_ARGS(&folder))))
        {
            dialog->SetFolder(folder);
            folder->Release();
        }
        uint8_t succeeded=0;
        if (SUCCEEDED(dialog->Show(static_cast<HWND>(ownerWindow))))
        {
            IShellItem* item=nullptr;
            if (SUCCEEDED(dialog->GetResult(&item)))
            {
                PWSTR path=nullptr;
                if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH,&path)))
                {
                    const auto utf8=WideToUtf8(path);
                    if (utf8.size()+1<=capacity)
                    {
                        memcpy(result,utf8.c_str(),utf8.size()+1);
                        succeeded=1;
                    }
                    CoTaskMemFree(path);
                }
                item->Release();
            }
        }
        dialog->Release();
        return succeeded;
    }
    void __cdecl SetUiText(const char* id, const char* text) { if (GUi) GUi->SetText(id,text); }
    void __cdecl SetUiMarkup(const char* id, const char* markup) { if (GUi) GUi->SetMarkup(id,markup); }
    void __cdecl SetUiProperty(const char* id, const char* name, const char* value) { if (GUi) GUi->SetProperty(id,name,value); }
    void* __cdecl CreateDockWindow(const char* hostId, const char* title)
    {
        return GUi && hostId && title ? GUi->CreateDockWindow(hostId,title) : nullptr;
    }
    uint8_t __cdecl DestroyDockWindow(const char* hostId)
    {
        return GUi && hostId && GUi->DestroyDockWindow(hostId) ? 1 : 0;
    }
    void* __cdecl CreateEditorWindow(const char* hostId, const char* title)
    {
        return GUi && hostId && title ? GUi->CreateEditorWindow(hostId, title) : nullptr;
    }
    void* __cdecl CreateOwnedDockWindow(const char* rootId, const char* hostId,
                                        const char* title)
    {
        return GUi && rootId && hostId && title ?
            GUi->CreateOwnedDockWindow(rootId, hostId, title) : nullptr;
    }
    uint8_t __cdecl DestroyEditorWindow(const char* rootId)
    {
        return GUi && rootId && GUi->DestroyEditorWindow(rootId) ? 1 : 0;
    }
    uint8_t __cdecl SetHostMarkup(const char* hostId, const char* id,
                                  const char* markup)
    {
        return GUi && hostId && id && markup &&
            GUi->SetHostMarkup(hostId,id,markup) ? 1 : 0;
    }
    uint8_t __cdecl SetHostUiProperty(const char* hostId, const char* id,
                                      const char* name, const char* value)
    {
        return GUi && hostId && id && name && value &&
            GUi->SetHostUiProperty(hostId,id,name,value) ? 1 : 0;
    }
    uint8_t __cdecl GetViewportRect(float* x,float* y,float* w,float* h)
    {
        UiViewportRect r{};
        if (!GUi || !x || !y || !w || !h || !GUi->GetViewport(r)) return 0;
        *x=r.X; *y=r.Y; *w=r.Width; *h=r.Height; return 1;
    }
    uint8_t __cdecl SetHostUiDocument(const char* HostId, const char* AnchorId,
        const char* Rml, const char* SourcePath, float Width, float Height)
    {
        return GUi && HostId && AnchorId && Rml && SourcePath &&
            GUi->SetHostUiDocument(HostId, AnchorId, Rml, SourcePath, Width, Height) ? 1 : 0;
    }
    uint8_t __cdecl GetUiElementRect(const char* id, float* x, float* y,
                                     float* w, float* h)
    {
        UiViewportRect r{};
        if (!GUi || !id || !x || !y || !w || !h ||
            !GUi->GetUiElementRect(id, r)) return 0;
        *x=r.X; *y=r.Y; *w=r.Width; *h=r.Height; return 1;
    }
    uint32_t __cdecl GetHostUiElementLayout(const char* HostId, const char* Id, char* Result, uint32_t Capacity)
    {
        if (!GUi || !HostId || !Id)
        {
            return 0;
        }
        const auto Text = GUi->GetHostUiElementLayout(HostId, Id);
        const auto Required = static_cast<uint32_t>(Text.size() + 1);
        if (Result && Capacity >= Required)
        {
            memcpy(Result, Text.c_str(), Required);
        }
        return Required;
    }
    uint8_t __cdecl GetHostUiElementRect(const char* hostId, const char* id,
                                         float* x, float* y, float* w, float* h)
    {
        UiViewportRect r{};
        if (!GUi || !hostId || !id || !x || !y || !w || !h ||
            !GUi->GetHostUiElementRect(hostId, id, r))
        {
            return 0;
        }
        *x = r.X;
        *y = r.Y;
        *w = r.Width;
        *h = r.Height;
        return 1;
    }
    void __cdecl SubmitViewportFrame(const float* triangles,uint32_t triangleVertices,const float* lines,uint32_t lineVertices)
    {
        UiViewportRect r{}; if (!GUi || !GUi->GetViewport(r)) return;
        GUi->SubmitScene(r,reinterpret_cast<const UiSceneVertex*>(triangles),static_cast<int>(triangleVertices),reinterpret_cast<const UiSceneVertex*>(lines),static_cast<int>(lineVertices));
    }
    void __cdecl SubmitTimelinePreviewFrame(const float* triangles,
                                            uint32_t triangleVertices,
                                            const float* lines,
                                            uint32_t lineVertices)
    {
        if (!GUi) return;
        GUi->SubmitTimelineScene(reinterpret_cast<const UiSceneVertex*>(triangles),
            static_cast<int>(triangleVertices),
            reinterpret_cast<const UiSceneVertex*>(lines),
            static_cast<int>(lineVertices));
    }
    void __cdecl SubmitHostViewportFrame(const char* hostId, const char* kind,
                                         const float* triangles,
                                         uint32_t triangleVertices,
                                         const float* lines,
                                         uint32_t lineVertices)
    {
        if (!GUi || !hostId || !kind)
        {
            return;
        }
        GUi->SubmitHostScene(hostId, kind,
            reinterpret_cast<const UiSceneVertex*>(triangles),
            static_cast<int>(triangleVertices),
            reinterpret_cast<const UiSceneVertex*>(lines),
            static_cast<int>(lineVertices));
    }
    uint8_t __cdecl EnsureGpuMesh(const GpuMeshUpload* upload)
    {
        return GUi && GUi->EnsureGpuMesh(upload) ? 1 : 0;
    }
    uint8_t __cdecl SubmitGpuScene(const GpuSceneFrame* frame)
    {
        return GUi && GUi->SubmitGpuScene(frame) ? 1 : 0;
    }
    uint8_t __cdecl GetRenderStats(RenderStats* stats)
    {
        return GUi && stats && GUi->GetRenderStats(*stats) ? 1 : 0;
    }
    uint8_t __cdecl SetGameplayInputCapture(uint8_t Enabled)
    {
        return GUi && GUi->SetGameplayInputCapture(Enabled != 0) ? 1 : 0;
    }
    uint8_t __cdecl GetGameplayInputCapture()
    {
        return GUi && GUi->IsGameplayInputCaptured() ? 1 : 0;
    }
    void __cdecl ViewportInput(void* windowHandle, int32_t x, int32_t y,
                               uint32_t inputEvent, uint32_t buttons,
                               int32_t wheelDelta)
    {
        if (GManaged) GManaged->ViewportInput(windowHandle, x, y, inputEvent, buttons, wheelDelta);
    }
    uint32_t CopyUtf8Result(const std::string& value, char* result,
                            uint32_t capacity)
    {
        if (value.size() >= UINT32_MAX) return 0;
        const auto required=static_cast<uint32_t>(value.size()+1);
        if (result && capacity>=required)
            memcpy(result,value.c_str(),required);
        return required;
    }
    uint32_t __cdecl GetUiSnapshot(uint64_t layoutRevision,
                                    uint64_t documentRevision,
                                    char* result, uint32_t capacity)
    {
        return GUi ? CopyUtf8Result(GUi->DescribeUi(layoutRevision,documentRevision, result == nullptr),
                                     result,capacity) : 0;
    }
    uint32_t __cdecl UiPointer(const char* hostId, uint32_t event,
                               int32_t x, int32_t y, uint32_t button,
                               uint32_t buttons, uint32_t modifiers,
                               uint32_t pointerId, uint64_t expectedFrameId,
                               float wheelX, float wheelY,
                               const char* expectedElementId,
                               char* result, uint32_t capacity)
    {
        if (!GUi || !hostId) return 0;
        return CopyUtf8Result(GUi->PointerInput(hostId,event,x,y,button,buttons,
            modifiers,pointerId,expectedFrameId,wheelX,wheelY,
            expectedElementId ? expectedElementId : ""),result,capacity);
    }
    uint32_t __cdecl UiKey(const char* hostId, uint32_t event, uint32_t key,
                           uint32_t modifiers, const char* text,
                           uint64_t expectedFrameId,
                           char* result, uint32_t capacity)
    {
        if (!GUi || !hostId) return 0;
        return CopyUtf8Result(GUi->KeyInput(hostId,event,key,modifiers,
            text ? text : "",expectedFrameId),result,capacity);
    }
    uint32_t __cdecl ReadUiFrame(uint64_t frameId, const char* hostId,
                                 uint8_t* pixels, uint32_t capacity,
                                 uint32_t* width, uint32_t* height)
    {
        if (!GUi || !hostId || !width || !height) return 0;
        return GUi->ReadFrame(frameId,hostId,pixels,capacity,*width,*height);
    }
    std::filesystem::path ExecutableDirectory()
    {
        std::wstring path(32768,L'\0');
        path.resize(GetModuleFileNameW(nullptr,path.data(),static_cast<DWORD>(path.size())));
        return std::filesystem::path(path).parent_path();
    }
    std::filesystem::path FindEngineRoot(
        const std::filesystem::path& executableDirectory)
    {
        for (auto candidate=executableDirectory; !candidate.empty();
             candidate=candidate.parent_path())
        {
            if (std::filesystem::exists(candidate/L"engine.json"))
                return candidate;
            if (candidate==candidate.root_path()) break;
        }
        return {};
    }

}
int wmain(int argc,wchar_t** argv)
{
    RenderOptions renderOptions;
    std::string renderError;
    std::filesystem::path projectManifest;
    uint32_t frameLimit=0;
    std::filesystem::path screenshot;
    bool mcpStdio=false;
    bool mcpIsolated=false;
    bool McpVisible = false;
    bool NoSplash = false;
    bool BrowserRequested = false;
    for (int i=1;i<argc;++i)
    {
        const std::wstring_view argument(argv[i]);
        if (renderOptions.Parse(i, argc, argv, renderError))
        {
            continue;
        }
        if (argument==L"--project" && i+1<argc) projectManifest=argv[++i];
        else if (argument==L"--frames" && i+1<argc) frameLimit=static_cast<uint32_t>(std::stoul(argv[++i]));
        else if (argument==L"--smoke-test") frameLimit=10;
        else if (argument==L"--screenshot" && i+1<argc) screenshot=argv[++i];
        else if (argument==L"--mcp-stdio") mcpStdio=true;
        else if (argument==L"--mcp-isolated") mcpIsolated=true;
        else if (argument == L"--mcp-visible") McpVisible = true;
        else if (argument == L"--project-browser")
        {
            BrowserRequested = true;
        }
        else if (argument == L"--no-splash")
        {
            NoSplash = true;
        }
    }
    if (!renderError.empty())
    {
        EngineLog::Error("Editor", renderError);
        return 2;
    }
    const auto executableDirectory=ExecutableDirectory();
    auto engineRoot=FindEngineRoot(executableDirectory);
    if (engineRoot.empty())
    {
        EngineLog::Error("Editor", "Could not locate engine.json from " + executableDirectory.string());
        return -1;
    }
    SetEnvironmentVariableW(L"SHARPENGINE_ROOT", engineRoot.c_str());
    if (BrowserRequested && (mcpStdio || !projectManifest.empty()))
    {
        EngineLog::Error("ProjectBrowser", "Project browser cannot be combined with --project or --mcp-stdio");
        return -1;
    }
    const bool BrowserMode = BrowserRequested || (projectManifest.empty() && !mcpStdio);
    if (BrowserMode && mcpIsolated && !BrowserRequested)
    {
        EngineLog::Error("Editor", "EDITOR_PROJECT_REQUIRED: Isolated Editor requires --project");
        return -1;
    }
    StartupSplash Splash(!BrowserMode && !mcpStdio && !mcpIsolated && !NoSplash, projectManifest.stem().wstring());
    ManagedProjectSelection selection;
    if (!BrowserMode && !projectManifest.empty())
    {
        if (!ResolveManagedProject(projectManifest,selection)) return -1;
        engineRoot=selection.EngineRoot;
        SetEnvironmentVariableW(L"SHARPENGINE_PROJECT_MANIFEST", selection.ProjectManifest.c_str());
    }
    else SetEnvironmentVariableW(L"SHARPENGINE_PROJECT_MANIFEST", nullptr);
    SetEnvironmentVariableW(L"SHARPENGINE_ROOT", engineRoot.c_str());
    EngineLog::Configure(selection.ProjectRoot.empty() ? engineRoot : selection.ProjectRoot,
                         BrowserMode ? "ProjectBrowser" : "Editor", mcpStdio);

    if (mcpStdio)
    {
        // The stdio process is only a session selector and bridge. It must not
        // initialize an EditorRuntime or execute project Modules before the
        // user explicitly attaches to an existing or isolated Editor.
        ManagedHost managed;
        const bool loaded=managed.Initialize(
            (executableDirectory/L"SharpEngine.Host.runtimeconfig.json").wstring(),
            (executableDirectory/L"SharpEngine.Host.dll").wstring());
        if (!loaded)
        {
            EngineLog::Error("MCP", "Managed bridge initialization failed");
            managed.Shutdown();
            return -1;
        }
        const int result=managed.RunMcp();
        managed.Shutdown();
        return result;
    }

    SetCurrentProcessExplicitAppUserModelID(L"SharpEngine.Editor");
    Splash.Report(8, "Platform", "Initializing native editor window");
    Platform::Initialize();
    const uint32_t WindowWidth = BrowserMode ? 1100 : 1440;
    const uint32_t WindowHeight = BrowserMode ? 760 : 900;
    auto window=Platform::CreateMainWindow(WindowWidth, WindowHeight,
        BrowserMode ? L"SharpEngine Project Browser" : L"SharpEngine Editor", false);
    if (!window.Window) { Platform::Shutdown(); return -1; }
    if (mcpIsolated) ShowWindow(static_cast<HWND>(window.Window),SW_HIDE);
    RmlUiShell ui;
    Splash.Report(16, "RenderCore / RmlUi", "Initializing graphics, fonts and editor shell");
    if (!ui.Initialize(static_cast<HWND>(window.Window), engineRoot,
        renderOptions.Backend, renderOptions.EnableVSync,
        renderOptions.EnableValidation, BrowserMode ? "Content/UI/Rml/ProjectBrowser.rml" : "Content/UI/Rml/Editor.rml"))
    {
        EngineLog::Error("RmlUi", "Initialization failed");
        ui.Shutdown(); Platform::Shutdown(); return -1;
    }
    GUi=&ui;
    ManagedHost managed;
    GManaged=&managed;
    ManagedHostApi api{};
    api.SetWindowTitle = SetWindowTitleUtf8;
    api.SetUiText = SetUiText;
    api.GetViewportRect = GetViewportRect;
    api.SubmitViewportFrame = SubmitViewportFrame;
    api.SetUiMarkup = SetUiMarkup;
    api.SetUiProperty = SetUiProperty;
    api.ViewportInput = ViewportInput;
    api.CreateDockWindow = CreateDockWindow;
    api.DestroyDockWindow = DestroyDockWindow;
    api.SetHostMarkup = SetHostMarkup;
    api.ChooseWorldSavePath = ChooseWorldSavePath;
    api.GetUiSnapshot = GetUiSnapshot;
    api.UiPointer = UiPointer;
    api.UiKey = UiKey;
    api.ReadUiFrame = ReadUiFrame;
    api.ReportStartupProgress = ReportStartupProgress;
    api.GetUiElementRect = GetUiElementRect;
    api.SubmitTimelinePreviewFrame = SubmitTimelinePreviewFrame;
    api.CreateEditorWindow = CreateEditorWindow;
    api.CreateOwnedDockWindow = CreateOwnedDockWindow;
    api.DestroyEditorWindow = DestroyEditorWindow;
    api.GetHostUiElementRect = GetHostUiElementRect;
    api.SubmitHostViewportFrame = SubmitHostViewportFrame;
    api.EnsureGpuMesh = EnsureGpuMesh;
    api.SubmitGpuScene = SubmitGpuScene;
    api.GetRenderStats = GetRenderStats;
    api.SetGameplayInputCapture = SetGameplayInputCapture;
    api.GetGameplayInputCapture = GetGameplayInputCapture;
    api.SetHostUiProperty = SetHostUiProperty;
    api.SetHostUiDocument = SetHostUiDocument;
    api.GetHostUiElementLayout = GetHostUiElementLayout;
    Splash.Report(28, "CoreCLR / SharpEngine.Host", "Loading .NET runtime and managed host");
    const auto ManagedDirectory = BrowserMode ? executableDirectory.parent_path() / L"Tools" : executableDirectory;
    const bool loaded=managed.Initialize(
        (ManagedDirectory / (BrowserMode ? L"SharpEngine.ProjectSelector.runtimeconfig.json" : L"SharpEngine.Host.runtimeconfig.json")).wstring(),
        (ManagedDirectory / (BrowserMode ? L"SharpEngine.ProjectSelector.dll" : L"SharpEngine.Host.dll")).wstring(),
        BrowserMode ? L"SharpEngine.ProjectSelector.BrowserEntrypoint, SharpEngine.ProjectSelector" : L"");
    GSplash = &Splash;
    const bool Started = loaded && managed.Start(window.Window, WindowWidth, WindowHeight, &api);
    GSplash = nullptr;
    if (!Started)
    {
        EngineLog::Error("ManagedHost", "Initialization failed");
        GManaged=nullptr; GUi=nullptr; ui.Shutdown(); Platform::Shutdown(); return -1;
    }
    ui.SetActionHandler([&managed, &ui, BrowserMode](const char* hostId,
                                        const char* action) {
        const std::string_view command = action ? action : "";
        const std::string_view host = hostId ? hostId : "";
        const HWND hostWindow = ui.GetHostWindow(host);
        if (command == "window.minimize")
        {
            if (hostWindow) ShowWindow(hostWindow, SW_MINIMIZE);
            return;
        }
        if (command == "window.maximize")
        {
            if (hostWindow)
                ShowWindow(hostWindow, IsZoomed(hostWindow) ? SW_RESTORE : SW_MAXIMIZE);
            return;
        }
        if (command == "window.close")
        {
            if (hostWindow) PostMessageW(hostWindow, WM_CLOSE, 0, 0);
            return;
        }
        if (BrowserMode)
        {
            managed.InvokeUiAction(action);
            return;
        }
        std::string routed="editor.host-action:";
        routed.append(host).push_back(':');
        routed.append(command);
        managed.InvokeUiAction(routed.c_str());
    });
    ui.SetInputEventHandler([&managed](const char* hostId, uint32_t eventKind,
        uint32_t code, int32_t x, int32_t y, float valueX, float valueY,
        uint32_t buttons, uint32_t modifiers, bool uiHandled,
        bool viewportFocused) {
        managed.InputEvent(hostId, eventKind, code, x, y, valueX, valueY,
            buttons, modifiers, uiHandled ? 1 : 0, viewportFocused ? 1 : 0);
    });
    Splash.Report(98, "Editor", "Preparing the first editor frame");
    auto previous=std::chrono::steady_clock::now();
    uint32_t frames=0;
    bool renderFailed=false;
    bool running=true;
    while (running)
    {
        running=Platform::PollEvents();
        if (!running) break;
        const auto now=std::chrono::steady_clock::now();
        managed.Update(std::chrono::duration<float>(now-previous).count()); previous=now;
        if (!ui.Render()) { EngineLog::Error("RmlUi", "Present failed"); renderFailed=true; break; }
        if (frames == 0)
        {
            Splash.Report(100, BrowserMode ? "ProjectBrowser" : "Editor", BrowserMode ? "Project browser ready" : "Editor ready");
            if (!mcpIsolated || McpVisible)
            {
                ui.ShowWindows();
            }
            Splash.Close();
        }
        ++frames; if (frameLimit && frames>=frameLimit) break;
        // Rendering and presentation count toward the minimum frame interval.
        std::this_thread::sleep_until(now + std::chrono::milliseconds(8));
    }
    EngineLog::Info(BrowserMode ? "ProjectBrowser" : "Editor", "Frames rendered=" + std::to_string(frames));
    if (!screenshot.empty() && !ui.CaptureScreenshot(screenshot))
    { EngineLog::Error("RmlUi", "Screenshot write failed"); renderFailed=true; }
    managed.Shutdown(); GUi=nullptr; ui.Shutdown(); Platform::Shutdown();
    GManaged=nullptr;
    return renderFailed ? -1 : 0;
}
