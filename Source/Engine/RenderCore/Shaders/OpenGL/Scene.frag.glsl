#version 330 core

in vec3 WorldPosition;
flat in vec4 SurfaceColor;

uniform int LightCount;
uniform vec3 SkyRadiance;
uniform vec3 LightDirections[16];
uniform vec3 LightRadiances[16];
uniform int SelectionPass;

layout(location = 0) out vec4 FinalColor;

void main()
{
    vec3 Dx = dFdx(WorldPosition);
    vec3 Dy = dFdy(WorldPosition);
    vec3 Normal = normalize(cross(Dx, Dy));
    vec3 Lighting = SkyRadiance;
    for (int Index = 0; Index < LightCount; ++Index)
    {
        float Diffuse = max(0.0, dot(Normal, -LightDirections[Index]));
        Lighting += LightRadiances[Index] * Diffuse;
    }
    FinalColor = SelectionPass != 0 ? SurfaceColor :
        vec4(clamp(SurfaceColor.rgb * Lighting, 0.0, 1.0), SurfaceColor.a);
}
