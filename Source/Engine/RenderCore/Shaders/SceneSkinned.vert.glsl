#version 450

layout(location = 0) in vec3 InPosition;
layout(location = 2) in uvec4 InBoneIndices;
layout(location = 3) in vec4 InBoneWeights;
layout(location = 9) in uvec4 InBoneIndices1;
layout(location = 10) in vec4 InBoneWeights1;
layout(location = 11) in uvec4 InBoneIndices2;
layout(location = 12) in vec4 InBoneWeights2;

struct InstanceData
{
    mat4 Model;
    vec4 Color;
    uint BoneOffset;
    uint BoneCount;
    uint Flags;
    uint Padding;
};

layout(set = 0, binding = 0, std140) uniform FrameData
{
    mat4 ViewProjection;
    vec4 LightDirections[16];
    vec4 LightRadiances[16];
    ivec4 Counts;
} Frame;

layout(set = 0, binding = 1, std430) readonly buffer BoneBuffer
{
    mat4 Bones[];
};

layout(set = 0, binding = 2, std430) readonly buffer InstanceBuffer
{
    InstanceData Instances[];
};

layout(location = 0) out vec3 WorldPosition;
layout(location = 1) flat out vec4 SurfaceColor;
layout(location = 2) flat out uint SelectionFlags;

mat4 WeightedSkin(uint Offset, uvec4 Indices, vec4 Weights)
{
    return Bones[Offset + Indices.x] * Weights.x + Bones[Offset + Indices.y] * Weights.y +
        Bones[Offset + Indices.z] * Weights.z + Bones[Offset + Indices.w] * Weights.w;
}

void main()
{
    InstanceData Instance = Instances[gl_InstanceIndex];
    float WeightTotal = dot(InBoneWeights + InBoneWeights1 + InBoneWeights2, vec4(1.0));
    mat4 Skin = mat4(1.0);
    if (WeightTotal > 0.0)
    {
        Skin = (WeightedSkin(Instance.BoneOffset, InBoneIndices, InBoneWeights) +
            WeightedSkin(Instance.BoneOffset, InBoneIndices1, InBoneWeights1) +
            WeightedSkin(Instance.BoneOffset, InBoneIndices2, InBoneWeights2)) / WeightTotal;
    }
    vec4 World = Instance.Model * Skin * vec4(InPosition, 1.0);
    WorldPosition = World.xyz;
    SurfaceColor = Instance.Color;
    SelectionFlags = Instance.Flags;
    gl_Position = Frame.ViewProjection * World;
    gl_Position.y = -gl_Position.y;
}
