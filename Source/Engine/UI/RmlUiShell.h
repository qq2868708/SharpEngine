#pragma once
#include <windows.h>
#include <filesystem>
#include <functional>
#include <memory>
#include <string>
#include <string_view>
#include "RenderCore/Public/PreviewRenderer.h"

struct UiViewportRect { float X, Y, Width, Height; };

// RmlUi owns layout, fonts, controls, clipping, focus, and input. Engine/game
// state is passed through small semantic text/action calls.
class RmlUiShell
{
public:
    RmlUiShell();
    ~RmlUiShell();
    bool Initialize(HWND window, const std::filesystem::path& engineRoot,
                    RenderBackendType backend = RenderBackendType::OpenGL,
                    bool enableVSync = true, bool enableValidation = false,
                    const std::filesystem::path& DocumentRelativePath = "Content/UI/Rml/Editor.rml");
    void Shutdown();
    bool Render(bool ObserveUi = false);
    void ShowWindows();
    HWND CreateDockWindow(std::string_view hostId, const char* titleUtf8);
    HWND CreateEditorWindow(std::string_view hostId, const char* titleUtf8);
    HWND CreateOwnedDockWindow(std::string_view rootId, std::string_view hostId,
                               const char* titleUtf8);
    bool DestroyDockWindow(std::string_view hostId);
    bool DestroyEditorWindow(std::string_view rootId);
    bool SetHostMarkup(std::string_view hostId, const char* elementId,
                       const char* markup);
    // Updates one RCSS property on an element of a specific host without replacing
    // markup, so a live design gesture keeps the native element identity it captured.
    bool SetHostUiProperty(std::string_view hostId, const char* elementId,
                           const char* name, const char* value);
    HWND GetHostWindow(std::string_view hostId) const;
    bool CaptureScreenshot(const std::filesystem::path& path);
    std::string DescribeUi(uint64_t layoutRevision, uint64_t documentRevision,
                           bool Refresh = true);
    std::string PointerInput(std::string_view hostId, uint32_t event,
                             int32_t x, int32_t y, uint32_t button,
                             uint32_t buttons, uint32_t modifiers,
                             uint32_t pointerId, uint64_t expectedFrameId,
                             float wheelX, float wheelY,
                             std::string_view expectedElementId);
    std::string KeyInput(std::string_view hostId, uint32_t event,
                         uint32_t key, uint32_t modifiers,
                         std::string_view text,
                         uint64_t expectedFrameId);
    uint32_t ReadFrame(uint64_t frameId, std::string_view hostId,
                       unsigned char* pixels, uint32_t capacity,
                       uint32_t& width, uint32_t& height) const;
    void RenderViewport(const UiViewportRect& rect,
                        std::string_view kind = "scene");
    void SetText(const char* id, const char* utf8);
    void SetMarkup(const char* id, const char* markup);
    void SetProperty(const char* id, const char* name, const char* value);
    bool GetViewport(UiViewportRect& rect) const;
    bool GetUiElementRect(std::string_view id, UiViewportRect& rect) const;
    bool GetHostUiElementRect(std::string_view hostId, std::string_view id,
                              UiViewportRect& rect) const;
    void SubmitScene(const UiViewportRect& rect, const UiSceneVertex* triangles,
                     int triangleVertices, const UiSceneVertex* lines, int lineVertices);
    bool EnsureGpuMesh(const GpuMeshUpload* upload);
    bool SubmitGpuScene(const GpuSceneFrame* frame);
    bool GetRenderStats(RenderStats& stats) const;
    bool SetGameplayInputCapture(bool enabled);
    bool IsGameplayInputCaptured() const;
    void SubmitTimelineScene(const UiSceneVertex* triangles, int triangleVertices,
                             const UiSceneVertex* lines, int lineVertices);
    void SubmitHostScene(std::string_view hostId, std::string_view kind,
                         const UiSceneVertex* triangles, int triangleVertices,
                         const UiSceneVertex* lines, int lineVertices);
    void SetActionHandler(
        std::function<void(const char* hostId, const char* action)> handler);
    void SetPointerHandler(std::function<void(int, int)> handler);
    // event: 0=move, 1=button down, 2=button up, 3=wheel.
    void SetViewportInputHandler(std::function<void(int, int, uint32_t, uint32_t, int32_t)> handler);
    // key is a Win32 virtual-key code. viewportFocused is derived from the
    // RmlUi focus tree, so text fields do not drive 3D camera movement.
    void SetViewportKeyHandler(std::function<void(uint32_t, bool, bool)> handler);
    void SetInputEventHandler(std::function<void(const char*, uint32_t, uint32_t,
        int32_t, int32_t, float, float, uint32_t, uint32_t, bool, bool)> handler);
    bool IsInitialized() const;
private:
    HWND CreateEditorHostWindow(std::string_view rootId, std::string_view hostId,
                                const char* titleUtf8, bool topLevelEditor);
    static LRESULT CALLBACK WindowProcedure(HWND window, UINT message,
                                            WPARAM wParam, LPARAM lParam);
    struct Impl;
    std::unique_ptr<Impl> State;
};
