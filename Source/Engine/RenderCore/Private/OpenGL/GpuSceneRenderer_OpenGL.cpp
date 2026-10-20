#include "GpuSceneRenderer_OpenGL.h"
#include "../../../Logging/EngineLog.h"
#include <RmlUi_Include_GL3.h>
#include <SceneShaders.generated.h>
#include <algorithm>
#include <array>
#include <cstddef>
#include <cstring>
#include <unordered_map>
#include <vector>

namespace
{
    constexpr uint32_t MaximumLights = 16;
    constexpr uint32_t MaximumBones = 256;

    struct ResourceHash
    {
        size_t operator()(const GpuResourceId& Resource) const
        {
            const size_t First = std::hash<uint64_t>{}(Resource.High);
            const size_t Second = std::hash<uint64_t>{}(Resource.Low);
            const size_t Third = std::hash<uint32_t>{}(Resource.Section);
            return First ^ (Second << 1) ^ (Third << 2);
        }
    };

    struct InstanceData
    {
        float Model[16];
        float Color[4];
    };

    GLuint CompileShader(GLenum Type, const char* Source)
    {
        const GLuint Shader = glCreateShader(Type);
        glShaderSource(Shader, 1, &Source, nullptr);
        glCompileShader(Shader);
        GLint Compiled = GL_FALSE;
        glGetShaderiv(Shader, GL_COMPILE_STATUS, &Compiled);
        if (Compiled == GL_TRUE)
        {
            return Shader;
        }
        GLint Length = 0;
        glGetShaderiv(Shader, GL_INFO_LOG_LENGTH, &Length);
        std::string Message(static_cast<size_t>((std::max)(Length, 1)), '\0');
        glGetShaderInfoLog(Shader, Length, nullptr, Message.data());
        EngineLog::Error("RenderCore", "OpenGL scene shader compile failed: " + Message);
        glDeleteShader(Shader);
        return 0;
    }

    GLuint LinkProgram(const char* VertexSource, const char* FragmentSource)
    {
        const GLuint Vertex = CompileShader(GL_VERTEX_SHADER, VertexSource);
        const GLuint Fragment = CompileShader(GL_FRAGMENT_SHADER, FragmentSource);
        if (!Vertex || !Fragment)
        {
            if (Vertex) glDeleteShader(Vertex);
            if (Fragment) glDeleteShader(Fragment);
            return 0;
        }
        const GLuint Program = glCreateProgram();
        glAttachShader(Program, Vertex);
        glAttachShader(Program, Fragment);
        glLinkProgram(Program);
        glDeleteShader(Vertex);
        glDeleteShader(Fragment);
        GLint Linked = GL_FALSE;
        glGetProgramiv(Program, GL_LINK_STATUS, &Linked);
        if (Linked == GL_TRUE)
        {
            return Program;
        }
        GLint Length = 0;
        glGetProgramiv(Program, GL_INFO_LOG_LENGTH, &Length);
        std::string Message(static_cast<size_t>((std::max)(Length, 1)), '\0');
        glGetProgramInfoLog(Program, Length, nullptr, Message.data());
        EngineLog::Error("RenderCore", "OpenGL scene program link failed: " + Message);
        glDeleteProgram(Program);
        return 0;
    }

}

struct GpuSceneRenderer_OpenGL::Impl
{
    struct Mesh
    {
        GLuint VertexArray = 0;
        GLuint VertexBuffer = 0;
        uint32_t VertexCount = 0;
        bool Skinned = false;
        uint64_t LastUsedFrame = 0;
    };

    GLuint StaticProgram = 0;
    GLuint SkinnedProgram = 0;
    GLuint InstanceBuffer = 0;
    GLuint BoneBuffer = 0;
    uint64_t FrameNumber = 0;
    std::unordered_map<GpuResourceId, Mesh, ResourceHash> Meshes;

    void DestroyMesh(Mesh& Value)
    {
        if (Value.VertexBuffer) glDeleteBuffers(1, &Value.VertexBuffer);
        if (Value.VertexArray) glDeleteVertexArrays(1, &Value.VertexArray);
        Value = {};
    }

    void BindLights(GLuint Program, const GpuSceneFrame& Frame)
    {
        glUniform3fv(glGetUniformLocation(Program, "SkyRadiance"), 1,
            Frame.SkyRadiance);
        const uint32_t Count = (std::min)(Frame.LightCount, MaximumLights);
        glUniform1i(glGetUniformLocation(Program, "LightCount"),
            static_cast<GLint>(Count));
        if (Count == 0)
        {
            return;
        }
        std::array<float, MaximumLights * 3> Directions{};
        std::array<float, MaximumLights * 3> Radiances{};
        for (uint32_t Index = 0; Index < Count; ++Index)
        {
            std::memcpy(Directions.data() + Index * 3,
                Frame.Lights[Index].Direction, sizeof(float) * 3);
            std::memcpy(Radiances.data() + Index * 3,
                Frame.Lights[Index].Radiance, sizeof(float) * 3);
        }
        glUniform3fv(glGetUniformLocation(Program, "LightDirections"),
            static_cast<GLsizei>(Count), Directions.data());
        glUniform3fv(glGetUniformLocation(Program, "LightRadiances"),
            static_cast<GLsizei>(Count), Radiances.data());
    }

    void ConfigureInstanceAttributes()
    {
        glBindBuffer(GL_ARRAY_BUFFER, InstanceBuffer);
        constexpr GLsizei Stride = sizeof(InstanceData);
        for (GLuint Column = 0; Column < 4; ++Column)
        {
            const GLuint Attribute = 4 + Column;
            glEnableVertexAttribArray(Attribute);
            glVertexAttribPointer(Attribute, 4, GL_FLOAT, GL_FALSE, Stride,
                reinterpret_cast<const void*>(sizeof(float) * 4 * Column));
            glVertexAttribDivisor(Attribute, 1);
        }
        glEnableVertexAttribArray(8);
        glVertexAttribPointer(8, 4, GL_FLOAT, GL_FALSE, Stride,
            reinterpret_cast<const void*>(sizeof(float) * 16));
        glVertexAttribDivisor(8, 1);
    }
};

GpuSceneRenderer_OpenGL::GpuSceneRenderer_OpenGL() : State(std::make_unique<Impl>()) {}
GpuSceneRenderer_OpenGL::~GpuSceneRenderer_OpenGL() { Shutdown(); }

bool GpuSceneRenderer_OpenGL::Initialize()
{
    State->StaticProgram = LinkProgram(OpenGLSceneStaticVert, OpenGLSceneFrag);
    State->SkinnedProgram = LinkProgram(OpenGLSceneSkinnedVert,
        OpenGLSceneFrag);
    if (!State->StaticProgram || !State->SkinnedProgram)
    {
        Shutdown();
        return false;
    }
    glGenBuffers(1, &State->InstanceBuffer);
    glGenBuffers(1, &State->BoneBuffer);
    glBindBuffer(GL_UNIFORM_BUFFER, State->BoneBuffer);
    glBufferData(GL_UNIFORM_BUFFER, MaximumBones * sizeof(float) * 16,
        nullptr, GL_STREAM_DRAW);
    const GLuint BoneBlock = glGetUniformBlockIndex(State->SkinnedProgram,
        "BonePalette");
    if (BoneBlock == GL_INVALID_INDEX)
    {
        EngineLog::Error("RenderCore", "OpenGL BonePalette block is unavailable");
        Shutdown();
        return false;
    }
    glUniformBlockBinding(State->SkinnedProgram, BoneBlock, 0);
    return State->InstanceBuffer != 0 && State->BoneBuffer != 0;
}

void GpuSceneRenderer_OpenGL::Shutdown()
{
    if (!State) return;
    for (auto& Pair : State->Meshes) State->DestroyMesh(Pair.second);
    State->Meshes.clear();
    if (State->InstanceBuffer) glDeleteBuffers(1, &State->InstanceBuffer);
    if (State->BoneBuffer) glDeleteBuffers(1, &State->BoneBuffer);
    if (State->StaticProgram) glDeleteProgram(State->StaticProgram);
    if (State->SkinnedProgram) glDeleteProgram(State->SkinnedProgram);
    State->InstanceBuffer = 0;
    State->BoneBuffer = 0;
    State->StaticProgram = 0;
    State->SkinnedProgram = 0;
}

bool GpuSceneRenderer_OpenGL::EnsureMesh(const GpuMeshUpload* Upload)
{
    if (!IsReady() || !Upload || !Upload->Vertices || Upload->VertexCount == 0)
        return false;
    if (State->Meshes.contains(Upload->Resource)) return true;
    Impl::Mesh Mesh;
    Mesh.VertexCount = Upload->VertexCount;
    Mesh.Skinned = Upload->Skinned != 0;
    glGenVertexArrays(1, &Mesh.VertexArray);
    glGenBuffers(1, &Mesh.VertexBuffer);
    if (!Mesh.VertexArray || !Mesh.VertexBuffer)
    {
        State->DestroyMesh(Mesh);
        return false;
    }
    glBindVertexArray(Mesh.VertexArray);
    glBindBuffer(GL_ARRAY_BUFFER, Mesh.VertexBuffer);
    glBufferData(GL_ARRAY_BUFFER,
        static_cast<GLsizeiptr>(sizeof(GpuSceneVertex) * Upload->VertexCount),
        Upload->Vertices, GL_STATIC_DRAW);
    glEnableVertexAttribArray(0);
    glVertexAttribPointer(0, 3, GL_FLOAT, GL_FALSE, sizeof(GpuSceneVertex),
        reinterpret_cast<const void*>(offsetof(GpuSceneVertex, Position)));
    glEnableVertexAttribArray(1);
    glVertexAttribPointer(1, 3, GL_FLOAT, GL_FALSE, sizeof(GpuSceneVertex),
        reinterpret_cast<const void*>(offsetof(GpuSceneVertex, Normal)));
    glEnableVertexAttribArray(2);
    glVertexAttribIPointer(2, 4, GL_UNSIGNED_INT, sizeof(GpuSceneVertex),
        reinterpret_cast<const void*>(offsetof(GpuSceneVertex, BoneIndices)));
    glEnableVertexAttribArray(3);
    glVertexAttribPointer(3, 4, GL_FLOAT, GL_FALSE, sizeof(GpuSceneVertex),
        reinterpret_cast<const void*>(offsetof(GpuSceneVertex, BoneWeights)));
    for (GLuint Group = 1; Group < 3; ++Group)
    {
        const GLuint IndexAttribute = 9 + (Group - 1) * 2;
        const GLuint WeightAttribute = IndexAttribute + 1;
        glEnableVertexAttribArray(IndexAttribute);
        glVertexAttribIPointer(IndexAttribute, 4, GL_UNSIGNED_INT,
            sizeof(GpuSceneVertex), reinterpret_cast<const void*>(
                offsetof(GpuSceneVertex, BoneIndices) + Group * 4 * sizeof(uint32_t)));
        glEnableVertexAttribArray(WeightAttribute);
        glVertexAttribPointer(WeightAttribute, 4, GL_FLOAT, GL_FALSE,
            sizeof(GpuSceneVertex), reinterpret_cast<const void*>(
                offsetof(GpuSceneVertex, BoneWeights) + Group * 4 * sizeof(float)));
    }
    State->Meshes.emplace(Upload->Resource, Mesh);
    return glGetError() == GL_NO_ERROR;
}

bool GpuSceneRenderer_OpenGL::Render(const GpuSceneFrame* Frame)
{
    if (!IsReady() || !Frame) return false;
    for (uint32_t Index = 0; Index < Frame->DrawCount; ++Index)
        if (!State->Meshes.contains(Frame->Draws[Index].Resource)) return false;

    GLint PreviousProgram = 0, PreviousVertexArray = 0, PreviousArrayBuffer = 0;
    GLint PreviousUniformBuffer = 0;
    GLint PreviousViewport[4]{}, PreviousScissor[4]{};
    glGetIntegerv(GL_CURRENT_PROGRAM, &PreviousProgram);
    glGetIntegerv(GL_VERTEX_ARRAY_BINDING, &PreviousVertexArray);
    glGetIntegerv(GL_ARRAY_BUFFER_BINDING, &PreviousArrayBuffer);
    glGetIntegerv(GL_UNIFORM_BUFFER_BINDING, &PreviousUniformBuffer);
    glGetIntegerv(GL_VIEWPORT, PreviousViewport);
    glGetIntegerv(GL_SCISSOR_BOX, PreviousScissor);
    const GLboolean DepthEnabled = glIsEnabled(GL_DEPTH_TEST);
    const GLboolean BlendEnabled = glIsEnabled(GL_BLEND);
    const GLboolean StencilEnabled = glIsEnabled(GL_STENCIL_TEST);
    const GLboolean ScissorEnabled = glIsEnabled(GL_SCISSOR_TEST);

    const GLint ViewportX = static_cast<GLint>(Frame->ViewportX);
    const GLint ViewportY = static_cast<GLint>(Frame->ClientHeight -
        Frame->ViewportY - Frame->ViewportHeight);
    const GLsizei ViewportWidth = static_cast<GLsizei>(Frame->ViewportWidth);
    const GLsizei ViewportHeight = static_cast<GLsizei>(Frame->ViewportHeight);
    glViewport(ViewportX, ViewportY, ViewportWidth, ViewportHeight);
    glEnable(GL_SCISSOR_TEST);
    glScissor(ViewportX, ViewportY, ViewportWidth, ViewportHeight);
    glEnable(GL_DEPTH_TEST);
    glDepthFunc(GL_LEQUAL);
    glDisable(GL_BLEND);
    glDisable(GL_STENCIL_TEST);
    glClearColor(0.055f, 0.065f, 0.08f, 1.0f);
    glClear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);

    ++State->FrameNumber;
    std::unordered_map<GpuResourceId, std::vector<const GpuSceneDraw*>, ResourceHash>
        StaticDraws;
    std::vector<const GpuSceneDraw*> SkinnedDraws;
    for (uint32_t Index = 0; Index < Frame->DrawCount; ++Index)
    {
        const auto& Draw = Frame->Draws[Index];
        auto& Mesh = State->Meshes.at(Draw.Resource);
        Mesh.LastUsedFrame = State->FrameNumber;
        if (Mesh.Skinned) SkinnedDraws.push_back(&Draw);
        else StaticDraws[Draw.Resource].push_back(&Draw);
    }

    glUseProgram(State->StaticProgram);
    glUniformMatrix4fv(glGetUniformLocation(State->StaticProgram,
        "ViewProjection"), 1, GL_FALSE, Frame->ViewProjection);
    State->BindLights(State->StaticProgram, *Frame);
    glUniform1i(glGetUniformLocation(State->StaticProgram, "SelectionPass"), 0);
    for (const auto& Pair : StaticDraws)
    {
        const auto& Mesh = State->Meshes.at(Pair.first);
        std::vector<InstanceData> Instances;
        Instances.reserve(Pair.second.size());
        for (const auto* Draw : Pair.second)
        {
            InstanceData Instance{};
            std::memcpy(Instance.Model, Draw->Model, sizeof(Instance.Model));
            std::memcpy(Instance.Color, Draw->Color, sizeof(Instance.Color));
            Instances.push_back(Instance);
        }
        glBindVertexArray(Mesh.VertexArray);
        glBindBuffer(GL_ARRAY_BUFFER, State->InstanceBuffer);
        glBufferData(GL_ARRAY_BUFFER,
            static_cast<GLsizeiptr>(Instances.size() * sizeof(InstanceData)),
            Instances.data(), GL_STREAM_DRAW);
        State->ConfigureInstanceAttributes();
        glDrawArraysInstanced(GL_TRIANGLES, 0,
            static_cast<GLsizei>(Mesh.VertexCount),
            static_cast<GLsizei>(Instances.size()));
    }

    glUseProgram(State->SkinnedProgram);
    glUniformMatrix4fv(glGetUniformLocation(State->SkinnedProgram,
        "ViewProjection"), 1, GL_FALSE, Frame->ViewProjection);
    State->BindLights(State->SkinnedProgram, *Frame);
    glUniform1i(glGetUniformLocation(State->SkinnedProgram, "SelectionPass"), 0);
    for (const auto* Draw : SkinnedDraws)
    {
        const auto& Mesh = State->Meshes.at(Draw->Resource);
        if (Draw->SkinMatrixCount > MaximumBones ||
            Draw->SkinMatrixOffset + Draw->SkinMatrixCount > Frame->SkinMatrixCount)
            return false;
        glBindVertexArray(Mesh.VertexArray);
        glUniformMatrix4fv(glGetUniformLocation(State->SkinnedProgram, "Model"),
            1, GL_FALSE, Draw->Model);
        glUniform4fv(glGetUniformLocation(State->SkinnedProgram, "DrawColor"),
            1, Draw->Color);
        if (Draw->SkinMatrixCount > 0)
        {
            glBindBuffer(GL_UNIFORM_BUFFER, State->BoneBuffer);
            glBufferSubData(GL_UNIFORM_BUFFER, 0,
                static_cast<GLsizeiptr>(Draw->SkinMatrixCount * sizeof(float) * 16),
                Frame->SkinMatrices + static_cast<size_t>(Draw->SkinMatrixOffset) * 16);
            glBindBufferBase(GL_UNIFORM_BUFFER, 0, State->BoneBuffer);
        }
        glDrawArrays(GL_TRIANGLES, 0, static_cast<GLsizei>(Mesh.VertexCount));
    }

    const std::array<float, 4> SelectionColor{1.0f, 0.68f, 0.18f, 1.0f};
    glPolygonMode(GL_FRONT_AND_BACK, GL_LINE);
    glEnable(GL_POLYGON_OFFSET_LINE);
    glPolygonOffset(-1.0f, -1.0f);
    glLineWidth(1.5f);
    for (uint32_t Index = 0; Index < Frame->DrawCount; ++Index)
    {
        const auto& Draw = Frame->Draws[Index];
        if ((Draw.Flags & 1u) == 0) continue;
        const auto& Mesh = State->Meshes.at(Draw.Resource);
        glBindVertexArray(Mesh.VertexArray);
        if (!Mesh.Skinned)
        {
            glUseProgram(State->StaticProgram);
            glUniform1i(glGetUniformLocation(State->StaticProgram,
                "SelectionPass"), 1);
            InstanceData Instance{};
            std::memcpy(Instance.Model, Draw.Model, sizeof(Instance.Model));
            std::memcpy(Instance.Color, SelectionColor.data(),
                sizeof(Instance.Color));
            glBindBuffer(GL_ARRAY_BUFFER, State->InstanceBuffer);
            glBufferData(GL_ARRAY_BUFFER, sizeof(Instance), &Instance,
                GL_STREAM_DRAW);
            State->ConfigureInstanceAttributes();
            glDrawArraysInstanced(GL_TRIANGLES, 0,
                static_cast<GLsizei>(Mesh.VertexCount), 1);
        }
        else
        {
            glUseProgram(State->SkinnedProgram);
            glUniform1i(glGetUniformLocation(State->SkinnedProgram,
                "SelectionPass"), 1);
            glUniformMatrix4fv(glGetUniformLocation(State->SkinnedProgram,
                "Model"), 1, GL_FALSE, Draw.Model);
            glUniform4fv(glGetUniformLocation(State->SkinnedProgram,
                "DrawColor"), 1, SelectionColor.data());
            if (Draw.SkinMatrixCount > 0)
            {
                glBindBuffer(GL_UNIFORM_BUFFER, State->BoneBuffer);
                glBufferSubData(GL_UNIFORM_BUFFER, 0,
                    static_cast<GLsizeiptr>(Draw.SkinMatrixCount * sizeof(float) * 16),
                    Frame->SkinMatrices + static_cast<size_t>(Draw.SkinMatrixOffset) * 16);
                glBindBufferBase(GL_UNIFORM_BUFFER, 0, State->BoneBuffer);
            }
            glDrawArrays(GL_TRIANGLES, 0,
                static_cast<GLsizei>(Mesh.VertexCount));
        }
    }
    glLineWidth(1.0f);
    glDisable(GL_POLYGON_OFFSET_LINE);
    glPolygonMode(GL_FRONT_AND_BACK, GL_FILL);

    for (auto Iterator = State->Meshes.begin(); Iterator != State->Meshes.end();)
    {
        if (State->FrameNumber > Iterator->second.LastUsedFrame + 3)
        {
            State->DestroyMesh(Iterator->second);
            Iterator = State->Meshes.erase(Iterator);
        }
        else ++Iterator;
    }

    glUseProgram(static_cast<GLuint>(PreviousProgram));
    glBindVertexArray(static_cast<GLuint>(PreviousVertexArray));
    glBindBuffer(GL_ARRAY_BUFFER, static_cast<GLuint>(PreviousArrayBuffer));
    glBindBuffer(GL_UNIFORM_BUFFER, static_cast<GLuint>(PreviousUniformBuffer));
    glViewport(PreviousViewport[0], PreviousViewport[1], PreviousViewport[2],
        PreviousViewport[3]);
    glScissor(PreviousScissor[0], PreviousScissor[1], PreviousScissor[2],
        PreviousScissor[3]);
    if (DepthEnabled) glEnable(GL_DEPTH_TEST); else glDisable(GL_DEPTH_TEST);
    if (BlendEnabled) glEnable(GL_BLEND); else glDisable(GL_BLEND);
    if (StencilEnabled) glEnable(GL_STENCIL_TEST); else glDisable(GL_STENCIL_TEST);
    if (ScissorEnabled) glEnable(GL_SCISSOR_TEST); else glDisable(GL_SCISSOR_TEST);
    return glGetError() == GL_NO_ERROR;
}

bool GpuSceneRenderer_OpenGL::IsReady() const
{
    return State && State->StaticProgram && State->SkinnedProgram &&
        State->InstanceBuffer && State->BoneBuffer;
}
