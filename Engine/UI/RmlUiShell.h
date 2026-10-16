#pragma once
#include <windows.h>
#include <filesystem>
#include <functional>
#include <memory>

struct UiViewportRect { float X, Y, Width, Height; };
struct UiSceneVertex { float X, Y, Z, R, G, B, A; };

// RmlUi owns layout, fonts, controls, clipping, focus, and input. Engine/game
// state is passed through small semantic text/action calls.
class RmlUiShell
{
public:
    RmlUiShell();
    ~RmlUiShell();
    bool Initialize(HWND window, const std::filesystem::path& engineRoot);
    void Shutdown();
    bool Render();
    bool CaptureScreenshot(const std::filesystem::path& path);
    void RenderViewport(const UiViewportRect& rect);
    void SetText(const char* id, const char* utf8);
    void SetMarkup(const char* id, const char* markup);
    void SetProperty(const char* id, const char* name, const char* value);
    bool GetViewport(UiViewportRect& rect) const;
    void SubmitScene(const UiViewportRect& rect, const UiSceneVertex* triangles,
                     int triangleVertices, const UiSceneVertex* lines, int lineVertices);
    void SetActionHandler(std::function<void(const char*)> handler);
    void SetPointerHandler(std::function<void(int, int)> handler);
    // event: 0=move, 1=button down, 2=button up, 3=wheel.
    void SetViewportInputHandler(std::function<void(int, int, uint32_t, uint32_t, int32_t)> handler);
    // key is a Win32 virtual-key code. viewportFocused is derived from the
    // RmlUi focus tree, so text fields do not drive 3D camera movement.
    void SetViewportKeyHandler(std::function<void(uint32_t, bool, bool)> handler);
    bool IsInitialized() const;
private:
    struct Impl;
    std::unique_ptr<Impl> State;
};
