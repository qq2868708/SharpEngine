#include <windows.h>
#include <chrono>
#include <filesystem>
#include <iostream>
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
    void __cdecl SetUiText(const char* id, const char* text) { if (GUi) GUi->SetText(id,text); }
    void __cdecl SetUiMarkup(const char* id, const char* markup) { if (GUi) GUi->SetMarkup(id,markup); }
    void __cdecl SetUiProperty(const char* id, const char* name, const char* value) { if (GUi) GUi->SetProperty(id,name,value); }
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
    std::filesystem::path ExecutableDirectory()
    {
        std::wstring path(32768,L'\0');
        path.resize(GetModuleFileNameW(nullptr,path.data(),static_cast<DWORD>(path.size())));
        return std::filesystem::path(path).parent_path();
    }
}
int wmain(int argc,wchar_t** argv)
{
    std::filesystem::path projectManifest;
    uint32_t frameLimit=0;
    std::filesystem::path screenshot;
    bool mcpStdio=false;
    for (int i=1;i<argc;++i)
    {
        const std::wstring_view argument(argv[i]);
        if (argument==L"--project" && i+1<argc) projectManifest=argv[++i];
        else if (argument==L"--frames" && i+1<argc) frameLimit=static_cast<uint32_t>(std::stoul(argv[++i]));
        else if (argument==L"--smoke-test") frameLimit=10;
        else if (argument==L"--screenshot" && i+1<argc) screenshot=argv[++i];
        else if (argument==L"--mcp-stdio") mcpStdio=true;
    }
    const auto executableDirectory=ExecutableDirectory();
    auto engineRoot=executableDirectory.parent_path().parent_path();
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
        // MCP is an engine-owned stdio transport. It starts the same managed
        // editor runtime as the windowed host, but never creates a desktop
        // window or relies on UI automation.
        ManagedHost managed;
        const ManagedHostApi api{5};
        const bool loaded=managed.Initialize(
            (executableDirectory/L"Managed/SharpEngine.Game.runtimeconfig.json").wstring(),
            (executableDirectory/L"Managed/SharpEngine.Game.dll").wstring());
        if (!loaded || !managed.Start(nullptr,0,0,&api))
        {
            std::cerr<<"[MCP] managed runtime initialization failed"<<std::endl;
            managed.Shutdown();
            return -1;
        }
        const int result=managed.RunMcp();
        managed.Shutdown();
        return result;
    }

    Platform::Initialize();
    auto window=Platform::CreateMainWindow(1440,900,L"SharpEngine Editor");
    if (!window.Window) { Platform::Shutdown(); return -1; }
    RmlUiShell ui;
    if (!ui.Initialize(static_cast<HWND>(window.Window),engineRoot))
    {
        std::cerr<<"[RmlUi] initialization failed"<<std::endl;
        ui.Shutdown(); Platform::Shutdown(); return -1;
    }
    GUi=&ui;
    ManagedHost managed;
    GManaged=&managed;
    const ManagedHostApi api{6,&SetWindowTitleUtf8,nullptr,nullptr,nullptr,nullptr,nullptr,nullptr,&SetUiText,&GetViewportRect,&SubmitViewportFrame,&SetUiMarkup,&SetUiProperty,&ViewportInput};
    const bool loaded=managed.Initialize(
        (executableDirectory/L"Managed/SharpEngine.Game.runtimeconfig.json").wstring(),
        (executableDirectory/L"Managed/SharpEngine.Game.dll").wstring());
    if (!loaded || !managed.Start(window.Window,1440,900,&api))
    {
        std::cerr<<"[ManagedHost] initialization failed"<<std::endl;
        GManaged=nullptr; GUi=nullptr; ui.Shutdown(); Platform::Shutdown(); return -1;
    }
    const HWND editorWindow = static_cast<HWND>(window.Window);
    ui.SetActionHandler([&managed, editorWindow](const char* action) {
        const std::string_view command = action ? action : "";
        if (command == "window.minimize")
        {
            ShowWindow(editorWindow, SW_MINIMIZE);
            return;
        }
        if (command == "window.maximize")
        {
            ShowWindow(editorWindow, IsZoomed(editorWindow) ? SW_RESTORE : SW_MAXIMIZE);
            return;
        }
        if (command == "window.close")
        {
            PostMessageW(editorWindow, WM_CLOSE, 0, 0);
            return;
        }
        managed.InvokeUiAction(action);
    });
    ui.SetPointerHandler([&managed, &window](int x, int y) { managed.PointerDown(window.Window, x, y, 1); });
    ui.SetViewportInputHandler(
        [&managed, &window](int x, int y, uint32_t inputEvent,
                            uint32_t buttons, int32_t wheelDelta) {
            managed.ViewportInput(window.Window, x, y, inputEvent, buttons, wheelDelta);
        });
    ui.SetViewportKeyHandler(
        [&managed](uint32_t key, bool pressed, bool viewportFocused) {
            managed.ViewportKey(key, pressed ? 1 : 0, viewportFocused ? 1 : 0);
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
