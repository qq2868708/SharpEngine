#pragma once

#include <cstddef>
#include <cstdint>
#include <cstring>
#include <vector>

struct GpuSceneVertex
{
    float Position[3];
    float Normal[3];
    uint32_t BoneIndices[12];
    float BoneWeights[12];
};

struct GpuResourceId
{
    uint64_t High;
    uint64_t Low;
    uint32_t Section;

    bool operator==(const GpuResourceId& Other) const
    {
        return High == Other.High && Low == Other.Low && Section == Other.Section;
    }
};

struct GpuMeshUpload
{
    GpuResourceId Resource;
    const GpuSceneVertex* Vertices;
    uint32_t VertexCount;
    uint8_t Skinned;
};

struct GpuDirectionalLight
{
    float Direction[3];
    float Radiance[3];
};

struct GpuSceneDraw
{
    GpuResourceId Resource;
    float Model[16];
    float Color[4];
    uint32_t SkinMatrixOffset;
    uint32_t SkinMatrixCount;
    uint32_t Flags;
};

struct GpuSceneFrame
{
    float ViewportX;
    float ViewportY;
    float ViewportWidth;
    float ViewportHeight;
    float ClientHeight;
    float SkyRadiance[3];
    float ViewProjection[16];
    const GpuDirectionalLight* Lights;
    uint32_t LightCount;
    const GpuSceneDraw* Draws;
    uint32_t DrawCount;
    const float* SkinMatrices;
    uint32_t SkinMatrixCount;
};

class GpuSceneFrameStorage
{
public:
    bool Assign(const GpuSceneFrame* Frame)
    {
        if (!Frame || Frame->ViewportWidth <= 0 || Frame->ViewportHeight <= 0 ||
            (Frame->LightCount > 0 && !Frame->Lights) ||
            (Frame->DrawCount > 0 && !Frame->Draws) ||
            (Frame->SkinMatrixCount > 0 && !Frame->SkinMatrices))
        {
            Ready = false;
            return false;
        }
        Value = *Frame;
        std::memcpy(ViewProjection, Frame->ViewProjection, sizeof(ViewProjection));
        if (Frame->LightCount > 0)
        {
            Lights.assign(Frame->Lights, Frame->Lights + Frame->LightCount);
        }
        else
        {
            Lights.clear();
        }
        if (Frame->DrawCount > 0)
        {
            Draws.assign(Frame->Draws, Frame->Draws + Frame->DrawCount);
        }
        else
        {
            Draws.clear();
        }
        if (Frame->SkinMatrixCount > 0)
        {
            SkinMatrices.assign(Frame->SkinMatrices,
                Frame->SkinMatrices + static_cast<size_t>(Frame->SkinMatrixCount) * 16);
        }
        else
        {
            SkinMatrices.clear();
        }
        Value.Lights = Lights.data();
        Value.Draws = Draws.data();
        Value.SkinMatrices = SkinMatrices.data();
        std::memcpy(Value.ViewProjection, ViewProjection, sizeof(ViewProjection));
        Ready = true;
        return true;
    }

    const GpuSceneFrame* Get() const { return Ready ? &Value : nullptr; }
    void Clear() { Ready = false; }

private:
    GpuSceneFrame Value{};
    float ViewProjection[16]{};
    std::vector<GpuDirectionalLight> Lights;
    std::vector<GpuSceneDraw> Draws;
    std::vector<float> SkinMatrices;
    bool Ready = false;
};

static_assert(sizeof(GpuSceneVertex) == 120);
static_assert(sizeof(GpuDirectionalLight) == 24);
static_assert(offsetof(GpuSceneFrame, SkyRadiance) == 20);
static_assert(offsetof(GpuSceneFrame, ViewProjection) == 32);
static_assert(offsetof(GpuSceneFrame, Lights) == 96);
static_assert(sizeof(GpuSceneFrame) == 144);
