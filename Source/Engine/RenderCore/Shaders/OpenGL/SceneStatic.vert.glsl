#version 330 core

layout(location = 0) in vec3 InPosition;
layout(location = 4) in vec4 InstanceModel0;
layout(location = 5) in vec4 InstanceModel1;
layout(location = 6) in vec4 InstanceModel2;
layout(location = 7) in vec4 InstanceModel3;
layout(location = 8) in vec4 InstanceColor;

uniform mat4 ViewProjection;

out vec3 WorldPosition;
flat out vec4 SurfaceColor;

void main()
{
    mat4 Model = mat4(InstanceModel0, InstanceModel1,
        InstanceModel2, InstanceModel3);
    vec4 World = Model * vec4(InPosition, 1.0);
    WorldPosition = World.xyz;
    SurfaceColor = InstanceColor;
    gl_Position = ViewProjection * World;
    gl_Position.z = gl_Position.z * 2.0 - gl_Position.w;
}
