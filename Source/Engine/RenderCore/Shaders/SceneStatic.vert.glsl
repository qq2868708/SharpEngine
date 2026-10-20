#version 450

layout(location = 0) in vec3 InPosition;

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

layout(set = 0, binding = 2, std430) readonly buffer InstanceBuffer
{
    InstanceData Instances[];
};

layout(location = 0) out vec3 WorldPosition;
layout(location = 1) flat out vec4 SurfaceColor;
layout(location = 2) flat out uint SelectionFlags;

void main()
{
    InstanceData Instance = Instances[gl_InstanceIndex];
    vec4 World = Instance.Model * vec4(InPosition, 1.0);
    WorldPosition = World.xyz;
    SurfaceColor = Instance.Color;
    SelectionFlags = Instance.Flags;
    gl_Position = Frame.ViewProjection * World;
    gl_Position.y = -gl_Position.y;
}
