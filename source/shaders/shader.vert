#version 450

layout(set = 0, binding = 0) uniform ModelViewProjection 
{
    mat4 Model;
    mat4 View;
    mat4 Project;
} mvp;

layout(location = 0) in vec3 position;
layout(location = 1) in vec3 color;

layout(location = 0) out vec3 fragColor;

void main() 
{
    gl_Position =  mvp.Project * mvp.View * mvp.Model * vec4(position, 1.0);
    fragColor = color;
}
