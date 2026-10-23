#pragma once
#include <windows.h>
#include <filesystem>
#include <memory>
#include <functional>
#include <string>
#include <string_view>
namespace Rml
{
    class RenderInterface;
}

// Runtime UI semantics are independent of the selected graphics API.
class GameUiShell
{
public:
    GameUiShell();
    ~GameUiShell();
    bool Initialize(HWND window, const std::filesystem::path& root, Rml::RenderInterface* renderer);
    void Shutdown();
    // Replaces the visible runtime screen with RML text. sourcePath is the
    // authoritative asset file, so relative stylesheet links resolve next to it.
    bool SetDocument(std::string_view rml, std::string_view sourcePath);
    // Removes the visible screen. The context stays alive for input/observation.
    bool ClearDocument();
    bool HasDocument() const;
    void Update(uint32_t width, uint32_t height);
    void Render();
    bool HandleMessage(UINT message, WPARAM wParam, LPARAM lParam, bool GameplayCaptured = false);
    bool IsFocused() const;
    uint32_t InputModifiers(uint32_t nativeModifiers) const;
    void SetPresentHandler(std::function<bool()> handler);
    // Receives semantic actions (the nearest ancestor `action`/`data-action` of a
    // click). Actions are queued and flushed before the next context update, so the
    // managed side may replace the document without unloading it mid-dispatch.
    void SetActionHandler(std::function<void(const char*, const char*)> handler);
    void CommitPresentation();
    std::string DescribeUi(uint64_t layoutRevision, uint64_t documentRevision);
    std::string PointerInput(std::string_view hostId, uint32_t event, int32_t x, int32_t y, uint32_t button, uint32_t buttons,
                             uint32_t modifiers, uint32_t pointerId, uint64_t expectedFrameId, float wheelX, float wheelY,
                             std::string_view expectedElementId);
    std::string KeyInput(std::string_view hostId, uint32_t event, uint32_t key, uint32_t modifiers, std::string_view text,
                         uint64_t expectedFrameId);
    uint32_t ReadFrame(uint64_t frameId, std::string_view hostId, unsigned char* pixels, uint32_t capacity, uint32_t& width,
                       uint32_t& height) const;

private:
    struct Impl;
    std::unique_ptr<Impl> State;
};
