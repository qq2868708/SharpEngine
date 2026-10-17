#pragma once
#include "../RenderBackend.h"
#include <memory>

class RenderBackend_Vulkan : public RenderBackend
{
public:
    RenderBackend_Vulkan();
    ~RenderBackend_Vulkan() override;

    bool Initialize(const RenderInitDesc* desc) override;
    void Shutdown() override;

    void BeginFrame() override;
    void SubmitFrame(const RenderFrame* frame) override;
    void EndFrame() override;

    void Resize(uint32_t width, uint32_t height) override;
    void RequestScreenshot(const wchar_t* path) override;
    bool HandleWindowMessage(void* window, uint32_t message,
                             uintptr_t wParam, intptr_t lParam) override;
    bool IsUiFocused() const override;

private:
#ifdef SHARPENGINE_GAME
    struct GameState;
    std::unique_ptr<GameState> State;
#endif
};
