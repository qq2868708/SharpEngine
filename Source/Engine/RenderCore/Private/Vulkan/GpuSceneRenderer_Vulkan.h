#pragma once

#include <memory>

struct GpuMeshUpload;
struct GpuSceneFrame;
struct RmlUiVulkanFrameContext;

class GpuSceneRenderer_Vulkan
{
public:
    GpuSceneRenderer_Vulkan();
    ~GpuSceneRenderer_Vulkan();

    bool EnsureMesh(const RmlUiVulkanFrameContext& Context,
        const GpuMeshUpload* Upload);
    bool Render(const RmlUiVulkanFrameContext& Context,
        const GpuSceneFrame* Frame);
    void Shutdown();

private:
    struct Impl;
    std::unique_ptr<Impl> State;
};
