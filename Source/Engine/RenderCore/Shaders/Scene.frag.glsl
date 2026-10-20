#version 450

layout(set = 0, binding = 0, std140) uniform FrameData
{
    mat4 ViewProjection;
    vec4 SkyRadiance;
    vec4 LightDirections[16];
    vec4 LightRadiances[16];
    ivec4 Counts;
} Frame;

layout(location = 0) in vec3 WorldPosition;
layout(location = 1) flat in vec4 SurfaceColor;
layout(location = 2) flat in uint SelectionFlags;
layout(location = 0) out vec4 FinalColor;

layout(push_constant) uniform DrawOptions
{
    uint SelectionPass;
} Options;

void main()
{
    vec3 Dx = dFdx(WorldPosition);
    vec3 Dy = dFdy(WorldPosition);
    vec3 Normal = normalize(cross(Dy, Dx));
    vec3 Lighting = Frame.SkyRadiance.xyz;
    for (int Index = 0; Index < Frame.Counts.x; ++Index)
    {
        float Diffuse = max(0.0, dot(Normal,
            -Frame.LightDirections[Index].xyz));
        Lighting += Frame.LightRadiances[Index].xyz * Diffuse;
    }
    if (Options.SelectionPass != 0)
    {
        FinalColor = vec4(1.0, 0.68, 0.18, 1.0);
    }
    else
    {
        FinalColor = vec4(clamp(SurfaceColor.rgb * Lighting, 0.0, 1.0),
            SurfaceColor.a);
    }
}
