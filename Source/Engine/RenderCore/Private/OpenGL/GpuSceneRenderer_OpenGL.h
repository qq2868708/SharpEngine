#pragma once

#include "../../Public/GpuScene.h"
#include <cstdint>
#include <memory>

class GpuSceneRenderer_OpenGL
{
public:
    GpuSceneRenderer_OpenGL();
    ~GpuSceneRenderer_OpenGL();

    bool Initialize();
    void Shutdown();
    bool EnsureMesh(const GpuMeshUpload* Upload);
    bool Render(const GpuSceneFrame* Frame);
    bool IsReady() const;

private:
    struct Impl;
    std::unique_ptr<Impl> State;
};
