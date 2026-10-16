#pragma once

#include <cstdint>
#include <filesystem>
#include <string>

using ManagedSetWindowTitleFn = void(__cdecl*)(void* windowHandle,
                                               const char* titleUtf8);
using ManagedBeginUiFn = void(__cdecl*)(void* windowHandle,
                                        uint32_t width,
                                        uint32_t height);
using ManagedUiPanelFn = void(__cdecl*)(float x, float y, float width,
                                        float height, uint8_t r, uint8_t g,
                                        uint8_t b, uint8_t a);
using ManagedUiTextFn = void(__cdecl*)(float x, float y, float width,
                                       float height, const char* textUtf8,
                                       uint8_t r, uint8_t g, uint8_t b,
                                       uint8_t a);
using ManagedUiButtonFn = void(__cdecl*)(float x, float y, float width,
                                         float height, const char* textUtf8,
                                         uint8_t r, uint8_t g, uint8_t b,
                                         uint8_t a, uint8_t enabled);
using ManagedEndUiFn = void(__cdecl*)(void* windowHandle);
using ManagedUiInputFn = void(__cdecl*)(float x, float y, float width,
                                        float height, const char* textUtf8,
                                        uint8_t r, uint8_t g, uint8_t b,
                                        uint8_t a, uint8_t enabled);

using ManagedSetUiTextFn = void(__cdecl*)(const char* id, const char* textUtf8);
using ManagedGetViewportRectFn = uint8_t(__cdecl*)(float* x, float* y, float* width, float* height);
using ManagedSubmitViewportFrameFn = void(__cdecl*)(const float* triangles, uint32_t triangleVertices, const float* lines, uint32_t lineVertices);
using ManagedSetUiMarkupFn = void(__cdecl*)(const char* id, const char* markupUtf8);
using ManagedSetUiPropertyFn = void(__cdecl*)(const char* id, const char* nameUtf8, const char* valueUtf8);
// event: 0=move, 1=button down, 2=button up, 3=wheel. buttons uses MK_* bits.
using ManagedViewportInputFn = void(__cdecl*)(void* windowHandle,
                                              int32_t x, int32_t y,
                                              uint32_t event, uint32_t buttons,
                                              int32_t wheelDelta);

struct ManagedHostApi
{
    uint32_t Version = 4;
    ManagedSetWindowTitleFn SetWindowTitle = nullptr;
    ManagedBeginUiFn BeginUi = nullptr;
    ManagedUiPanelFn UiPanel = nullptr;
    ManagedUiTextFn UiText = nullptr;
    ManagedUiButtonFn UiButton = nullptr;
    ManagedEndUiFn EndUi = nullptr;
    ManagedUiInputFn UiInput = nullptr;
    ManagedSetUiTextFn SetUiText = nullptr;
    ManagedGetViewportRectFn GetViewportRect = nullptr;
    ManagedSubmitViewportFrameFn SubmitViewportFrame = nullptr;
    ManagedSetUiMarkupFn SetUiMarkup = nullptr;
    ManagedSetUiPropertyFn SetUiProperty = nullptr;
    ManagedViewportInputFn ViewportInput = nullptr;
};

// Resolved from a project Project/project.json manifest. Paths are kept on the
// native side so the managed assembly only receives the normal lifecycle API.
struct ManagedProjectSelection
{
    std::filesystem::path ProjectManifest;
    std::filesystem::path ProjectRoot;
    std::filesystem::path EngineRoot;
    std::filesystem::path RuntimeConfigPath;
    std::filesystem::path AssemblyPath;
    std::wstring EntryPointTypeName;
};

bool ResolveManagedProject(const std::filesystem::path& projectManifest,
                           ManagedProjectSelection& selection);

// The native host owns the .NET runtime lifetime. The managed side only sees
// the small, POD-shaped calls exposed by this class.
class ManagedHost
{
public:
    ManagedHost() = default;
    ~ManagedHost();

    ManagedHost(const ManagedHost&) = delete;
    ManagedHost& operator=(const ManagedHost&) = delete;

    bool Initialize(const std::wstring& runtimeConfigPath,
                    const std::wstring& assemblyPath,
                    const std::wstring& entryPointTypeName = {});
    bool Start(void* windowHandle,
               uint32_t width,
               uint32_t height,
               const ManagedHostApi* api);
    void Update(float deltaSeconds);
    void PointerDown(void* windowHandle, int32_t x, int32_t y,
                     uint32_t button);
    void InvokeUiAction(const char* actionUtf8);
    void ViewportInput(void* windowHandle, int32_t x, int32_t y,
                       uint32_t event, uint32_t buttons, int32_t wheelDelta);
    void ViewportKey(uint32_t key, uint8_t pressed, uint8_t viewportFocused);
    // Runs the embedded MCP stdio transport. The managed entry point owns
    // JSON-RPC dispatch so the same serialized scene/asset model is exposed
    // to AI clients without desktop automation.
    int RunMcp();
    void Shutdown();

    bool IsInitialized() const { return RuntimeContext != nullptr; }

private:
    void ResetRuntime();

    void* HostFxrModule = nullptr;
    void* RuntimeContext = nullptr;
    void* LoadAssemblyAndGetFunctionPointerFunction = nullptr;

    void* ManagedInitializeFunction = nullptr;
    void* ManagedUpdateFunction = nullptr;
    void* ManagedShutdownFunction = nullptr;
    void* ManagedPointerDownFunction = nullptr;
    void* ManagedInvokeUiActionFunction = nullptr;
    void* ManagedViewportInputFunction = nullptr;
    void* ManagedViewportKeyFunction = nullptr;
    void* ManagedRunMcpFunction = nullptr;
};

