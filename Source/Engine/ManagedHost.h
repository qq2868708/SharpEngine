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
using ManagedInputEventFn = void(__cdecl*)(const char* hostIdUtf8,
                                           uint32_t eventKind,
                                           uint32_t code,
                                           int32_t x, int32_t y,
                                           float valueX, float valueY,
                                           uint32_t buttons,
                                           uint32_t modifiers,
                                           uint8_t uiHandled,
                                           uint8_t viewportFocused);
using ManagedCreateDockWindowFn = void*(__cdecl*)(const char* hostIdUtf8,
                                                   const char* titleUtf8);
using ManagedDestroyDockWindowFn = uint8_t(__cdecl*)(const char* hostIdUtf8);
using ManagedSetHostMarkupFn = uint8_t(__cdecl*)(const char* hostIdUtf8,
                                                  const char* elementIdUtf8,
                                                  const char* markupUtf8);
using ManagedChooseWorldSavePathFn = uint8_t(__cdecl*)(void* ownerWindow,
                                                        const char* contentRootUtf8,
                                                        char* resultUtf8,
                                                        uint32_t resultCapacity);
using ManagedGetUiSnapshotFn = uint32_t(__cdecl*)(uint64_t layoutRevision,
                                                   uint64_t documentRevision,
                                                   char* resultUtf8,
                                                   uint32_t resultCapacity);
using ManagedUiPointerFn = uint32_t(__cdecl*)(const char* hostIdUtf8,
                                               uint32_t event, int32_t x, int32_t y,
                                               uint32_t button, uint32_t buttons,
                                               uint32_t modifiers, uint32_t pointerId,
                                               uint64_t expectedFrameId,
                                               float wheelX, float wheelY,
                                               const char* expectedElementIdUtf8,
                                               char* resultUtf8,
                                               uint32_t resultCapacity);
using ManagedUiKeyFn = uint32_t(__cdecl*)(const char* hostIdUtf8,
                                           uint32_t event, uint32_t key,
                                           uint32_t modifiers, const char* textUtf8,
                                           uint64_t expectedFrameId,
                                           char* resultUtf8,
                                           uint32_t resultCapacity);
using ManagedReadUiFrameFn = uint32_t(__cdecl*)(uint64_t frameId,
                                                 const char* hostIdUtf8,
                                                 uint8_t* pixels,
                                                 uint32_t pixelCapacity,
                                                 uint32_t* width,
                                                 uint32_t* height);

struct ManagedHostApi
{
    uint32_t Version = 9;
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
    // Version 7 appends transient native Dock Host operations. Field order is
    // ABI-significant and must match Source/SharpEngine.Editor/Editor/NativeEngineApi.cs.
    ManagedCreateDockWindowFn CreateDockWindow = nullptr;
    ManagedDestroyDockWindowFn DestroyDockWindow = nullptr;
    ManagedSetHostMarkupFn SetHostMarkup = nullptr;
    // Version 8 appends the project-scoped .sworld Save As dialog.
    ManagedChooseWorldSavePathFn ChooseWorldSavePath = nullptr;
    // Version 9 appends live RmlUi observation and input. All calls execute on
    // the Editor thread; returned IDs and JSON never expose native handles.
    ManagedGetUiSnapshotFn GetUiSnapshot = nullptr;
    ManagedUiPointerFn UiPointer = nullptr;
    ManagedUiKeyFn UiKey = nullptr;
    ManagedReadUiFrameFn ReadUiFrame = nullptr;
};

// Resolved from a root .sharpproject manifest. User Module loading remains in
// the engine-owned managed bootstrap; native code resolves only project/engine roots.
struct ManagedProjectSelection
{
    std::filesystem::path ProjectManifest;
    std::filesystem::path ProjectRoot;
    std::filesystem::path EngineRoot;
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
    void InputEvent(const char* hostIdUtf8, uint32_t eventKind, uint32_t code,
                    int32_t x, int32_t y, float valueX, float valueY,
                    uint32_t buttons, uint32_t modifiers,
                    uint8_t uiHandled, uint8_t viewportFocused);
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
    void* ManagedInputEventFunction = nullptr;
    void* ManagedRunMcpFunction = nullptr;
};

