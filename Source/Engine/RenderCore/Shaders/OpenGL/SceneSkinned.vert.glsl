#version 330 core

layout(location = 0) in vec3 InPosition;
layout(location = 2) in uvec4 InBoneIndices;
layout(location = 3) in vec4 InBoneWeights;
layout(location = 9) in uvec4 InBoneIndices1;
layout(location = 10) in vec4 InBoneWeights1;
layout(location = 11) in uvec4 InBoneIndices2;
layout(location = 12) in vec4 InBoneWeights2;

uniform mat4 ViewProjection;
uniform mat4 Model;
layout(std140) uniform BonePalette
{
    mat4 Bones[256];
};
uniform vec4 DrawColor;

out vec3 WorldPosition;
flat out vec4 SurfaceColor;

mat4 WeightedSkin(uvec4 Indices, vec4 Weights)
{
    return Bones[Indices.x] * Weights.x + Bones[Indices.y] * Weights.y +
        Bones[Indices.z] * Weights.z + Bones[Indices.w] * Weights.w;
}

void main()
{
    float WeightTotal = dot(InBoneWeights + InBoneWeights1 + InBoneWeights2, vec4(1.0));
    mat4 Skin = mat4(1.0);
    if (WeightTotal > 0.0)
    {
        Skin = (WeightedSkin(InBoneIndices, InBoneWeights) +
            WeightedSkin(InBoneIndices1, InBoneWeights1) +
            WeightedSkin(InBoneIndices2, InBoneWeights2)) / WeightTotal;
    }
    vec4 World = Model * Skin * vec4(InPosition, 1.0);
    WorldPosition = World.xyz;
    SurfaceColor = DrawColor;
    gl_Position = ViewProjection * World;
    gl_Position.z = gl_Position.z * 2.0 - gl_Position.w;
}
