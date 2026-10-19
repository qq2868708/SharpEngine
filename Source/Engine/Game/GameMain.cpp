#include <windows.h>
#include <windowsx.h>
#include <shobjidl.h>
#include <chrono>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <string>
#include <vector>
#include "ManagedHost.h"
#include "Platform/Windows/SharpEngineBranding.h"
#include "Logging/EngineLog.h"
#include "RenderCore/Public/RenderCore.h"
#include "RenderCore/Public/PreviewRenderer.h"
#include "RenderCore/Public/RenderOptions.h"
#include "RenderCore/Private/RenderBackend.h"
#include "UI/GameUiShell.h"

namespace
{
    ManagedHost* Managed{};
    GameUiShell* Ui{};
    HWND Window{};
    uint32_t RenderWidth = 1280;
    uint32_t RenderHeight = 720;
    POINT LastPointer{};
    bool PointerKnown{};
    std::vector<UiSceneVertex> Triangles, Lines;
    uint8_t __cdecl Viewport(float* x, float* y, float* w, float* h)
    {
        uint32_t width = RenderWidth;
        uint32_t height = RenderHeight;
        if (Window)
        {
            RECT r{};
            if (GetClientRect(Window, &r) && r.right > 0 && r.bottom > 0)
            {
                width = static_cast<uint32_t>(r.right);
                height = static_cast<uint32_t>(r.bottom);
            }
        }
        *x = 0;
        *y = 0;
        *w = static_cast<float>(width);
        *h = static_cast<float>(height);
        return width > 0 && height > 0;
    }
    void __cdecl Submit(const float* t, uint32_t nt, const float* l, uint32_t nl)
    {
        Triangles.clear();
        Lines.clear();
        if (nt)
        {
            auto* p = reinterpret_cast<const UiSceneVertex*>(t);
            Triangles.assign(p, p + nt);
        }
        if (nl)
        {
            auto* p = reinterpret_cast<const UiSceneVertex*>(l);
            Lines.assign(p, p + nl);
        }
    }
    uint32_t CopyUtf8Result(const std::string& value, char* result, uint32_t capacity)
    {
        if (value.size() >= UINT32_MAX)
        {
            return 0;
        }
        const auto required = static_cast<uint32_t>(value.size() + 1);
        if (result && capacity >= required)
        {
            memcpy(result, value.c_str(), required);
        }
        return required;
    }
    uint32_t __cdecl GetUiSnapshot(uint64_t layoutRevision, uint64_t documentRevision, char* result, uint32_t capacity)
    {
        return Ui ? CopyUtf8Result(Ui->DescribeUi(layoutRevision, documentRevision), result, capacity) : 0;
    }
    uint32_t __cdecl UiPointer(const char* hostId, uint32_t event, int32_t x, int32_t y, uint32_t button, uint32_t buttons,
                               uint32_t modifiers, uint32_t pointerId, uint64_t expectedFrameId, float wheelX, float wheelY,
                               const char* expectedElementId, char* result, uint32_t capacity)
    {
        if (!Ui || !hostId)
        {
            return 0;
        }
        return CopyUtf8Result(Ui->PointerInput(hostId, event, x, y, button, buttons, modifiers, pointerId, expectedFrameId,
                                               wheelX, wheelY, expectedElementId ? expectedElementId : ""),
                              result, capacity);
    }
    uint32_t __cdecl UiKey(const char* hostId, uint32_t event, uint32_t key, uint32_t modifiers, const char* text,
                           uint64_t expectedFrameId, char* result, uint32_t capacity)
    {
        if (!Ui || !hostId)
        {
            return 0;
        }
        return CopyUtf8Result(Ui->KeyInput(hostId, event, key, modifiers, text ? text : "", expectedFrameId), result, capacity);
    }
    uint32_t __cdecl ReadUiFrame(uint64_t frameId, const char* hostId, uint8_t* pixels, uint32_t capacity, uint32_t* width,
                                 uint32_t* height)
    {
        if (!Ui || !hostId || !width || !height)
        {
            return 0;
        }
        return Ui->ReadFrame(frameId, hostId, pixels, capacity, *width, *height);
    }
    bool RenderGameFrame()
    {
        uint32_t width = RenderWidth;
        uint32_t height = RenderHeight;
        if (Window)
        {
            RECT bounds{};
            if (IsIconic(Window) || !GetClientRect(Window, &bounds) || bounds.right <= 0 || bounds.bottom <= 0)
            {
                return false;
            }
            width = static_cast<uint32_t>(bounds.right);
            height = static_cast<uint32_t>(bounds.bottom);
        }
        if (Ui)
        {
            Ui->Update(width, height);
        }
        RC_Resize(width, height);
        RC_BeginFrame();
        const RenderFrame frame
        {
            0, 0, static_cast<float>(width), static_cast<float>(height), static_cast<float>(height),
            {
                Triangles.data(), static_cast<uint32_t>(Triangles.size())
            },
            {
                Lines.data(), static_cast<uint32_t>(Lines.size())
            }
        };
        RC_SubmitFrame(&frame);
        if (Ui)
        {
            Ui->Render();
        }
        RC_EndFrame();
        if (Ui)
        {
            Ui->CommitPresentation();
        }
        return !Window || GetActiveRenderBackend()->WasPresented();
    }
    LRESULT CALLBACK Procedure(HWND window, UINT message, WPARAM wp, LPARAM lp)
    {
        const bool uiHandled = Ui && Ui->HandleMessage(message, wp, lp);
        if (message == WM_CLOSE)
        {
            PostQuitMessage(0);
            return 0;
        }
        if (message == WM_ERASEBKGND)
        {
            return 1;
        }
        if (Managed)
        {
            uint32_t modifiers = (GetKeyState(VK_SHIFT) < 0 ? 1u : 0u) | (GetKeyState(VK_CONTROL) < 0 ? 2u : 0u) |
                                 (GetKeyState(VK_MENU) < 0 ? 4u : 0u);
            if (Ui)
            {
                modifiers = Ui->InputModifiers(modifiers);
            }
            uint32_t kind = 99, code = 0;
            float dx = 0, dy = 0;
            int x = GET_X_LPARAM(lp), y = GET_Y_LPARAM(lp);
            if (message == WM_KEYDOWN || message == WM_SYSKEYDOWN)
            {
                kind = 0;
                code = static_cast<uint32_t>(wp);
            }
            if (message == WM_KEYUP || message == WM_SYSKEYUP)
            {
                kind = 1;
                code = static_cast<uint32_t>(wp);
            }
            if (message == WM_LBUTTONDOWN || message == WM_RBUTTONDOWN || message == WM_MBUTTONDOWN)
            {
                kind = 3;
                code = message == WM_LBUTTONDOWN ? 0 : message == WM_RBUTTONDOWN ? 1 : 2;
                SetCapture(window);
            }
            if (message == WM_LBUTTONUP || message == WM_RBUTTONUP || message == WM_MBUTTONUP)
            {
                kind = 4;
                code = message == WM_LBUTTONUP ? 0 : message == WM_RBUTTONUP ? 1 : 2;
                ReleaseCapture();
            }
            if (message == WM_XBUTTONDOWN || message == WM_XBUTTONUP)
            {
                kind = message == WM_XBUTTONDOWN ? 3 : 4;
                code = GET_XBUTTON_WPARAM(wp) == XBUTTON1 ? 3 : 4;
                if (kind == 3)
                {
                    SetCapture(window);
                }
                else
                {
                    ReleaseCapture();
                }
            }
            if (message == WM_MOUSEMOVE)
            {
                kind = 2;
                if (PointerKnown)
                {
                    dx = static_cast<float>(x - LastPointer.x);
                    dy = static_cast<float>(y - LastPointer.y);
                }
                LastPointer.x = x;
                LastPointer.y = y;
                PointerKnown = true;
            }
            if (message == WM_MOUSEWHEEL || message == WM_MOUSEHWHEEL)
            {
                kind = 5;
                POINT point
                {
                    x, y
                };
                ScreenToClient(window, &point);
                x = point.x;
                y = point.y;
                const float wheel = static_cast<float>(GET_WHEEL_DELTA_WPARAM(wp)) / WHEEL_DELTA;
                if (message == WM_MOUSEWHEEL)
                {
                    dy = wheel;
                }
                else
                {
                    dx = wheel;
                }
            }
            if (message == WM_KILLFOCUS)
            {
                kind = 6;
                PointerKnown = false;
            }
            if (message == WM_CAPTURECHANGED)
            {
                kind = 7;
            }
            if (kind != 99)
            {
                Managed->InputEvent("game", kind, code, x, y, dx, dy, 0, modifiers, uiHandled ? 1 : 0,
                                    Ui && Ui->IsFocused() ? 1 : 0);
            }
        }
        if (uiHandled)
        {
            return 0;
        }
        return DefWindowProcW(window, message, wp, lp);
    }
} // namespace

int wmain(int argc, wchar_t** argv)
{
    std::filesystem::path project, screenshot;
    uint32_t limit = 0;
    bool hidden = false;
    RenderOptions renderOptions;
    std::string renderError;
    bool mcpStdio = false;
    bool renderOffscreen = false;
    bool nullRhi = false;
    for (int i = 1; i < argc; ++i)
    {
        const std::wstring arg = argv[i];
        if (renderOptions.Parse(i, argc, argv, renderError))
        {
            continue;
        }
        if (arg == L"--project" && i + 1 < argc)
        {
            project = argv[++i];
        }
        else if (arg == L"--frames" && i + 1 < argc)
        {
            limit = std::stoul(argv[++i]);
        }
        else if (arg == L"--smoke-test")
        {
            limit = 30;
        }
        else if (arg == L"--screenshot" && i + 1 < argc)
        {
            screenshot = argv[++i];
        }
        else if (arg == L"--hidden")
        {
            hidden = true;
        }
        else if (arg == L"--mcp-stdio")
        {
            mcpStdio = true;
        }
        else if (arg == L"--render-offscreen" || arg == L"-RenderOffscreen")
        {
            renderOffscreen = true;
        }
        else if (arg == L"--null-rhi" || arg == L"-NullRHI")
        {
            nullRhi = true;
        }
    }
    if (!renderError.empty())
    {
        EngineLog::Error("Game", renderError);
        return 2;
    }
    if (renderOptions.Explicit && (renderOffscreen || nullRhi))
    {
        EngineLog::Error("Game", "RENDER_BACKEND_CONFLICT: graphics and headless modes cannot be combined");
        return 2;
    }
    if (mcpStdio)
    {
        std::wstring executable(32768, L'\0');
        executable.resize(GetModuleFileNameW(nullptr, executable.data(), static_cast<DWORD>(executable.size())));
        const auto directory = std::filesystem::path(executable).parent_path();
        ManagedHost bridge;
        if (!bridge.Initialize((directory / L"SharpEngine.Host.runtimeconfig.json").wstring(),
                               (directory / L"SharpEngine.Host.dll").wstring()))
        {
            return 4;
        }
        return bridge.RunMcp();
    }
    if (project.empty())
    {
        EngineLog::Error("Game", "GAME_PROJECT_REQUIRED: use --project <file.sharpproject>");
        return 2;
    }
    if (renderOffscreen && nullRhi)
    {
        EngineLog::Error("Game", "RENDER_BACKEND_CONFLICT: RenderOffscreen and NullRHI cannot be selected together");
        return 2;
    }
    std::wstring exe(32768, L'\0');
    exe.resize(GetModuleFileNameW(nullptr, exe.data(), static_cast<DWORD>(exe.size())));
    const auto directory = std::filesystem::path(exe).parent_path();
    auto root = directory;
    while (!root.empty() && !std::filesystem::exists(root / L"engine.json"))
    {
        auto parent = root.parent_path();
        if (parent == root)
        {
            break;
        }
        root = parent;
    }
    SetEnvironmentVariableW(L"SHARPENGINE_ROOT", root.c_str());
    ManagedProjectSelection selection;
    if (!ResolveManagedProject(project, selection))
    {
        return 2;
    }
    SetEnvironmentVariableW(L"SHARPENGINE_PROJECT_MANIFEST", selection.ProjectManifest.c_str());
    SetEnvironmentVariableW(L"SHARPENGINE_ROOT", selection.EngineRoot.c_str());
    EngineLog::Configure(selection.ProjectRoot, "Game");
    const bool headless = renderOffscreen || nullRhi;
    if (headless && limit == 0)
    {
        limit = 1;
    }
    if (renderOffscreen && screenshot.empty())
    {
        screenshot = selection.ProjectRoot / L"Saved/Validation/RenderOffscreen/frame.bmp";
    }

    RenderBackendType backendType = renderOptions.Backend;
    if (renderOffscreen)
    {
        backendType = RenderBackendType::Offscreen;
    }
    if (nullRhi)
    {
        backendType = RenderBackendType::NullRHI;
    }

    if (!headless)
    {
        SetCurrentProcessExplicitAppUserModelID(L"SharpEngine.Game");
        WNDCLASSW wc{};
        wc.lpfnWndProc = Procedure;
        wc.hInstance = GetModuleHandleW(nullptr);
        wc.lpszClassName = L"SharpEngineGameWindow";
        wc.style = CS_OWNDC;
        wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
        wc.hIcon = SharpEngine::Windows::LoadBrandIcon(wc.hInstance, GetSystemMetrics(SM_CXICON), GetSystemMetrics(SM_CYICON));
        RegisterClassW(&wc);
        Window =
            CreateWindowW(wc.lpszClassName, L"SharpEngine Game", WS_OVERLAPPEDWINDOW, CW_USEDEFAULT, CW_USEDEFAULT,
                          static_cast<int>(RenderWidth), static_cast<int>(RenderHeight), nullptr, nullptr, wc.hInstance, nullptr);
        if (!Window)
        {
            return 3;
        }
        SharpEngine::Windows::ApplyBrandIcon(Window, wc.hInstance);
    }
    const RenderInitDesc renderDesc
    {
        Window, RenderWidth, RenderHeight, false, backendType
    };
    if (!RC_Initialize(&renderDesc))
    {
        if (Window)
        {
            DestroyWindow(Window);
        }
        return 3;
    }
    GameUiShell ui;
    if (!headless && !ui.Initialize(Window, selection.EngineRoot, GetActiveRenderBackend()->GetUiRenderer()))
    {
        ui.Shutdown();
        RC_Shutdown();
        DestroyWindow(Window);
        return 3;
    }
    Ui = headless ? nullptr : &ui;
    ManagedHost managed;
    ManagedHostApi api{};
    api.GetViewportRect = Viewport;
    api.SubmitViewportFrame = Submit;
    api.GetUiSnapshot = Ui ? GetUiSnapshot : nullptr;
    api.UiPointer = Ui ? UiPointer : nullptr;
    api.UiKey = Ui ? UiKey : nullptr;
    api.ReadUiFrame = Ui ? ReadUiFrame : nullptr;
    if (Ui)
    {
        Ui->SetPresentHandler(RenderGameFrame);
    }
    if (!managed.Initialize((directory / L"SharpEngine.Host.runtimeconfig.json").wstring(),
                            (directory / L"SharpEngine.Host.dll").wstring()) ||
        !managed.Start(Window, RenderWidth, RenderHeight, &api))
    {
        managed.Shutdown();
        Ui = nullptr;
        ui.Shutdown();
        RC_Shutdown();
        if (Window)
        {
            DestroyWindow(Window);
        }
        return 4;
    }
    Managed = &managed;
    if (Window && !hidden)
    {
        ShowWindow(Window, SW_SHOW);
    }
    auto last = std::chrono::steady_clock::now();
    uint32_t frames = 0;
    bool running = true;
    while (running)
    {
        if (Window)
        {
            MSG message{};
            while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
            {
                if (message.message == WM_QUIT)
                {
                    running = false;
                    break;
                }
                TranslateMessage(&message);
                DispatchMessageW(&message);
            }
        }
        if (!running)
        {
            break;
        }
        auto now = std::chrono::steady_clock::now();
        float delta = std::chrono::duration<float>(now - last).count();
        last = now;
        managed.Update(limit ? 1.0f / 60.0f : (delta > 0.1f ? 0.1f : delta));
        if (!RenderGameFrame())
        {
            Sleep(10);
            continue;
        }
        if (limit && frames + 1 >= limit && !screenshot.empty())
        {
            RC_RequestScreenshot(screenshot.c_str());
        }
        ++frames;
        if (limit && frames >= limit)
        {
            break;
        }
        Sleep(1);
    }
    RenderStats stats{};
    RC_GetStats(&stats);
    EngineLog::Info("Game", "Render backend completed frames=" + std::to_string(stats.FramesBegun) + " submitted=" +
                                std::to_string(stats.FramesSubmitted) + " triangles=" + std::to_string(stats.TriangleVertices) +
                                " lines=" + std::to_string(stats.LineVertices) +
                                " framebuffer=" + std::to_string(stats.HasFramebuffer));
    Managed = nullptr;
    managed.Shutdown();
    Ui = nullptr;
    ui.Shutdown();
    RC_Shutdown();
    if (Window)
    {
        DestroyWindow(Window);
    }
    return 0;
}
