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
#include "ManagedHost.h"
#include "UI/RmlUiShell.h"

namespace
{
    RmlUiShell* GUi{};
    ManagedHost* GManaged{};
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
    uint8_t __cdecl SetHostMarkup(const char* hostId, const char* id,
                                  const char* markup)
    {
        return GUi && hostId && id && markup &&
            GUi->SetHostMarkup(hostId,id,markup) ? 1 : 0;
    }
    uint8_t __cdecl GetViewportRect(float* x,float* y,float* w,float* h)
    {
        UiViewportRect r{};
        if (!GUi || !x || !y || !w || !h || !GUi->GetViewport(r)) return 0;
        *x=r.X; *y=r.Y; *w=r.Width; *h=r.Height; return 1;
    }
    void __cdecl SubmitViewportFrame(const float* triangles,uint32_t triangleVertices,const float* lines,uint32_t lineVertices)
    {
        UiViewportRect r{}; if (!GUi || !GUi->GetViewport(r)) return;
        GUi->SubmitScene(r,reinterpret_cast<const UiSceneVertex*>(triangles),static_cast<int>(triangleVertices),reinterpret_cast<const UiSceneVertex*>(lines),static_cast<int>(lineVertices));
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
        return GUi ? CopyUtf8Result(GUi->DescribeUi(layoutRevision,documentRevision),
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
    std::filesystem::path projectManifest;
    uint32_t frameLimit=0;
    std::filesystem::path screenshot;
    bool mcpStdio=false;
    bool mcpIsolated=false;
    for (int i=1;i<argc;++i)
    {
        const std::wstring_view argument(argv[i]);
        if (argument==L"--project" && i+1<argc) projectManifest=argv[++i];
        else if (argument==L"--frames" && i+1<argc) frameLimit=static_cast<uint32_t>(std::stoul(argv[++i]));
        else if (argument==L"--smoke-test") frameLimit=10;
        else if (argument==L"--screenshot" && i+1<argc) screenshot=argv[++i];
        else if (argument==L"--mcp-stdio") mcpStdio=true;
        else if (argument==L"--mcp-isolated") mcpIsolated=true;
    }
    const auto executableDirectory=ExecutableDirectory();
    auto engineRoot=FindEngineRoot(executableDirectory);
    if (engineRoot.empty())
    {
        std::cerr<<"[Editor] could not locate engine.json from "
                 <<executableDirectory<<std::endl;
        return -1;
    }
    SetEnvironmentVariableW(L"SHARPENGINE_ROOT", engineRoot.c_str());
    ManagedProjectSelection selection;
    if (!projectManifest.empty())
    {
        if (!ResolveManagedProject(projectManifest,selection)) return -1;
        engineRoot=selection.EngineRoot;
        SetEnvironmentVariableW(L"SHARPENGINE_PROJECT_MANIFEST", selection.ProjectManifest.c_str());
    }
    else SetEnvironmentVariableW(L"SHARPENGINE_PROJECT_MANIFEST", nullptr);
    SetEnvironmentVariableW(L"SHARPENGINE_ROOT", engineRoot.c_str());

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
            std::cerr<<"[MCP] managed bridge initialization failed"<<std::endl;
            managed.Shutdown();
            return -1;
        }
        const int result=managed.RunMcp();
        managed.Shutdown();
        return result;
    }

    SetCurrentProcessExplicitAppUserModelID(L"SharpEngine.Editor");
    Platform::Initialize();
    auto window=Platform::CreateMainWindow(1440,900,L"SharpEngine Editor");
    if (!window.Window) { Platform::Shutdown(); return -1; }
    if (mcpIsolated) ShowWindow(static_cast<HWND>(window.Window),SW_HIDE);
    RmlUiShell ui;
    if (!ui.Initialize(static_cast<HWND>(window.Window),engineRoot))
    {
        std::cerr<<"[RmlUi] initialization failed"<<std::endl;
        ui.Shutdown(); Platform::Shutdown(); return -1;
    }
    GUi=&ui;
    ManagedHost managed;
    GManaged=&managed;
    const ManagedHostApi api{9,&SetWindowTitleUtf8,nullptr,nullptr,nullptr,nullptr,nullptr,nullptr,&SetUiText,&GetViewportRect,&SubmitViewportFrame,&SetUiMarkup,&SetUiProperty,&ViewportInput,&CreateDockWindow,&DestroyDockWindow,&SetHostMarkup,&ChooseWorldSavePath,&GetUiSnapshot,&UiPointer,&UiKey,&ReadUiFrame};
    const bool loaded=managed.Initialize(
        (executableDirectory/L"SharpEngine.Host.runtimeconfig.json").wstring(),
        (executableDirectory/L"SharpEngine.Host.dll").wstring());
    if (!loaded || !managed.Start(window.Window,1440,900,&api))
    {
        std::cerr<<"[ManagedHost] initialization failed"<<std::endl;
        GManaged=nullptr; GUi=nullptr; ui.Shutdown(); Platform::Shutdown(); return -1;
    }
    ui.SetActionHandler([&managed, &ui](const char* hostId,
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
        if (!ui.Render()) { std::cerr<<"[RmlUi] present failed"<<std::endl; renderFailed=true; break; }
        ++frames; if (frameLimit && frames>=frameLimit) break;
        std::this_thread::sleep_for(std::chrono::milliseconds(8));
    }
    std::cout<<"[Editor] frames="<<frames<<std::endl;
    if (!screenshot.empty() && !ui.CaptureScreenshot(screenshot))
    { std::cerr<<"[RmlUi] screenshot write failed"<<std::endl; renderFailed=true; }
    managed.Shutdown(); GUi=nullptr; ui.Shutdown(); Platform::Shutdown();
    GManaged=nullptr;
    return renderFailed ? -1 : 0;
}
