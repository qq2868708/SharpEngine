#include "GpuSceneRenderer_Vulkan.h"
#include "../../../Logging/EngineLog.h"
#include "../../Public/GpuScene.h"
#include <RmlUi_Renderer_VK.h>
#include <SceneShaders.generated.h>
#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <exception>
#include <string>
#include <unordered_map>
#include <vector>

namespace
{
    constexpr uint32_t MaximumLights = 16;
    constexpr uint32_t FrameSlotCount = 3;

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

    struct Buffer
    {
        VkBuffer Handle = VK_NULL_HANDLE;
        VkDeviceMemory Memory = VK_NULL_HANDLE;
        void* Mapped = nullptr;
        VkDeviceSize Capacity = 0;
    };

    struct alignas(16) FrameData
    {
        float ViewProjection[16];
        float SkyRadiance[4];
        float LightDirections[MaximumLights][4];
        float LightRadiances[MaximumLights][4];
        int32_t Counts[4];
    };

    struct alignas(16) InstanceData
    {
        float Model[16];
        float Color[4];
        uint32_t BoneOffset;
        uint32_t BoneCount;
        uint32_t Flags;
        uint32_t Padding;
    };

    struct StaticBatch
    {
        GpuResourceId Resource{};
        std::vector<const GpuSceneDraw*> Draws;
        uint32_t FirstInstance = 0;
    };

    struct DrawRecord
    {
        GpuResourceId Resource{};
        const GpuSceneDraw* Draw = nullptr;
        uint32_t InstanceIndex = 0;
        bool Skinned = false;
    };

    bool ResultSucceeded(VkResult Result, const char* Operation)
    {
        if (Result == VK_ERROR_DEVICE_LOST)
        {
            EngineLog::Error("RenderCore", std::string(
                "Vulkan device lost during ") + Operation);
            std::terminate();
        }
        return Result == VK_SUCCESS;
    }

    uint32_t FindMemoryType(VkPhysicalDevice PhysicalDevice,
        uint32_t TypeBits, VkMemoryPropertyFlags Properties)
    {
        VkPhysicalDeviceMemoryProperties MemoryProperties{};
        vkGetPhysicalDeviceMemoryProperties(PhysicalDevice, &MemoryProperties);
        for (uint32_t Index = 0; Index < MemoryProperties.memoryTypeCount; ++Index)
        {
            const bool TypeMatches = (TypeBits & (1u << Index)) != 0;
            const bool PropertiesMatch =
                (MemoryProperties.memoryTypes[Index].propertyFlags & Properties) ==
                Properties;
            if (TypeMatches && PropertiesMatch)
            {
                return Index;
            }
        }
        return UINT32_MAX;
    }

    void DestroyBuffer(VkDevice Device, Buffer& Value)
    {
        if (Value.Mapped)
        {
            vkUnmapMemory(Device, Value.Memory);
        }
        if (Value.Handle)
        {
            vkDestroyBuffer(Device, Value.Handle, nullptr);
        }
        if (Value.Memory)
        {
            vkFreeMemory(Device, Value.Memory, nullptr);
        }
        Value = {};
    }

    bool CreateBuffer(const RmlUiVulkanFrameContext& Context,
        VkDeviceSize Capacity, VkBufferUsageFlags Usage, Buffer& Result)
    {
        VkBufferCreateInfo BufferInfo{};
        BufferInfo.sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO;
        BufferInfo.size = Capacity;
        BufferInfo.usage = Usage;
        BufferInfo.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
        if (!ResultSucceeded(vkCreateBuffer(Context.device, &BufferInfo, nullptr,
            &Result.Handle), "scene buffer creation"))
        {
            return false;
        }

        VkMemoryRequirements Requirements{};
        vkGetBufferMemoryRequirements(Context.device, Result.Handle, &Requirements);
        const VkMemoryPropertyFlags Properties =
            VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT |
            VK_MEMORY_PROPERTY_HOST_COHERENT_BIT;
        const uint32_t MemoryType = FindMemoryType(Context.physical_device,
            Requirements.memoryTypeBits, Properties);
        if (MemoryType == UINT32_MAX)
        {
            DestroyBuffer(Context.device, Result);
            return false;
        }

        VkMemoryAllocateInfo AllocateInfo{};
        AllocateInfo.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
        AllocateInfo.allocationSize = Requirements.size;
        AllocateInfo.memoryTypeIndex = MemoryType;
        const bool Allocated = ResultSucceeded(vkAllocateMemory(Context.device,
            &AllocateInfo, nullptr, &Result.Memory), "scene memory allocation");
        const bool Bound = Allocated && ResultSucceeded(vkBindBufferMemory(
            Context.device, Result.Handle, Result.Memory, 0),
            "scene buffer binding");
        const bool Mapped = Bound && ResultSucceeded(vkMapMemory(Context.device,
            Result.Memory, 0, Capacity, 0, &Result.Mapped),
            "scene buffer mapping");
        if (!Mapped)
        {
            DestroyBuffer(Context.device, Result);
            return false;
        }
        Result.Capacity = Capacity;
        return true;
    }

    bool EnsureBuffer(const RmlUiVulkanFrameContext& Context,
        VkDeviceSize Required, VkBufferUsageFlags Usage, Buffer& Value)
    {
        if (Value.Capacity >= Required)
        {
            return true;
        }
        DestroyBuffer(Context.device, Value);
        VkDeviceSize Capacity = 256;
        while (Capacity < Required)
        {
            Capacity *= 2;
        }
        return CreateBuffer(Context, Capacity, Usage, Value);
    }

    VkShaderModule CreateShaderModule(VkDevice Device, const uint32_t* Code,
        size_t Size)
    {
        VkShaderModuleCreateInfo Info{};
        Info.sType = VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO;
        Info.codeSize = Size;
        Info.pCode = Code;
        VkShaderModule Module = VK_NULL_HANDLE;
        if (!ResultSucceeded(vkCreateShaderModule(Device, &Info, nullptr,
            &Module), "scene shader module creation"))
        {
            return VK_NULL_HANDLE;
        }
        return Module;
    }
}

struct GpuSceneRenderer_Vulkan::Impl
{
    struct Mesh
    {
        Buffer VertexBuffer;
        uint32_t VertexCount = 0;
        bool Skinned = false;
        uint64_t LastUsedFrame = 0;
    };

    struct FrameResources
    {
        Buffer FrameBuffer;
        Buffer BoneBuffer;
        Buffer InstanceBuffer;
        VkDescriptorSet DescriptorSet = VK_NULL_HANDLE;
    };

    VkPhysicalDevice PhysicalDevice = VK_NULL_HANDLE;
    VkDevice Device = VK_NULL_HANDLE;
    VkDescriptorSetLayout DescriptorSetLayout = VK_NULL_HANDLE;
    VkDescriptorPool DescriptorPool = VK_NULL_HANDLE;
    VkPipelineLayout PipelineLayout = VK_NULL_HANDLE;
    VkPipeline StaticPipeline = VK_NULL_HANDLE;
    VkPipeline SkinnedPipeline = VK_NULL_HANDLE;
    VkPipeline StaticSelectionPipeline = VK_NULL_HANDLE;
    VkPipeline SkinnedSelectionPipeline = VK_NULL_HANDLE;
    uint64_t PipelineGeneration = 0;
    uint64_t FrameNumber = 0;
    std::array<FrameResources, FrameSlotCount> Frames;
    std::unordered_map<GpuResourceId, Mesh, ResourceHash> Meshes;

    bool EnsureDevice(const RmlUiVulkanFrameContext& Context)
    {
        if (Device == Context.device && PhysicalDevice == Context.physical_device)
        {
            return DescriptorSetLayout && DescriptorPool && PipelineLayout;
        }
        if (Device)
        {
            return false;
        }
        Device = Context.device;
        PhysicalDevice = Context.physical_device;
        return CreateDescriptorResources();
    }

    bool CreateDescriptorResources()
    {
        std::array<VkDescriptorSetLayoutBinding, 3> Bindings{};
        Bindings[0] = {0, VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER, 1,
            VK_SHADER_STAGE_VERTEX_BIT | VK_SHADER_STAGE_FRAGMENT_BIT, nullptr};
        Bindings[1] = {1, VK_DESCRIPTOR_TYPE_STORAGE_BUFFER, 1,
            VK_SHADER_STAGE_VERTEX_BIT, nullptr};
        Bindings[2] = {2, VK_DESCRIPTOR_TYPE_STORAGE_BUFFER, 1,
            VK_SHADER_STAGE_VERTEX_BIT, nullptr};
        VkDescriptorSetLayoutCreateInfo LayoutInfo{};
        LayoutInfo.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO;
        LayoutInfo.bindingCount = static_cast<uint32_t>(Bindings.size());
        LayoutInfo.pBindings = Bindings.data();
        if (!ResultSucceeded(vkCreateDescriptorSetLayout(Device, &LayoutInfo,
            nullptr, &DescriptorSetLayout),
            "scene descriptor layout creation"))
        {
            return false;
        }

        VkPushConstantRange PushRange{};
        PushRange.stageFlags = VK_SHADER_STAGE_FRAGMENT_BIT;
        PushRange.size = sizeof(uint32_t);
        VkPipelineLayoutCreateInfo PipelineLayoutInfo{};
        PipelineLayoutInfo.sType = VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO;
        PipelineLayoutInfo.setLayoutCount = 1;
        PipelineLayoutInfo.pSetLayouts = &DescriptorSetLayout;
        PipelineLayoutInfo.pushConstantRangeCount = 1;
        PipelineLayoutInfo.pPushConstantRanges = &PushRange;
        if (!ResultSucceeded(vkCreatePipelineLayout(Device, &PipelineLayoutInfo,
            nullptr, &PipelineLayout), "scene pipeline layout creation"))
        {
            return false;
        }

        const std::array<VkDescriptorPoolSize, 2> PoolSizes
        {{
            {VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER, FrameSlotCount},
            {VK_DESCRIPTOR_TYPE_STORAGE_BUFFER, FrameSlotCount * 2},
        }};
        VkDescriptorPoolCreateInfo PoolInfo{};
        PoolInfo.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO;
        PoolInfo.maxSets = FrameSlotCount;
        PoolInfo.poolSizeCount = static_cast<uint32_t>(PoolSizes.size());
        PoolInfo.pPoolSizes = PoolSizes.data();
        if (!ResultSucceeded(vkCreateDescriptorPool(Device, &PoolInfo, nullptr,
            &DescriptorPool), "scene descriptor pool creation"))
        {
            return false;
        }

        const std::array<VkDescriptorSetLayout, FrameSlotCount> Layouts
        {{
            DescriptorSetLayout, DescriptorSetLayout, DescriptorSetLayout,
        }};
        std::array<VkDescriptorSet, FrameSlotCount> DescriptorSets{};
        VkDescriptorSetAllocateInfo AllocateInfo{};
        AllocateInfo.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO;
        AllocateInfo.descriptorPool = DescriptorPool;
        AllocateInfo.descriptorSetCount = FrameSlotCount;
        AllocateInfo.pSetLayouts = Layouts.data();
        if (!ResultSucceeded(vkAllocateDescriptorSets(Device, &AllocateInfo,
            DescriptorSets.data()), "scene descriptor allocation"))
        {
            return false;
        }
        for (uint32_t Index = 0; Index < FrameSlotCount; ++Index)
        {
            Frames[Index].DescriptorSet = DescriptorSets[Index];
        }
        return true;
    }

    void DestroyPipelines()
    {
        if (StaticPipeline)
        {
            vkDestroyPipeline(Device, StaticPipeline, nullptr);
        }
        if (SkinnedPipeline)
        {
            vkDestroyPipeline(Device, SkinnedPipeline, nullptr);
        }
        if (StaticSelectionPipeline)
        {
            vkDestroyPipeline(Device, StaticSelectionPipeline, nullptr);
        }
        if (SkinnedSelectionPipeline)
        {
            vkDestroyPipeline(Device, SkinnedSelectionPipeline, nullptr);
        }
        StaticPipeline = VK_NULL_HANDLE;
        SkinnedPipeline = VK_NULL_HANDLE;
        StaticSelectionPipeline = VK_NULL_HANDLE;
        SkinnedSelectionPipeline = VK_NULL_HANDLE;
    }

    bool CreatePipeline(const RmlUiVulkanFrameContext& Context,
        VkShaderModule VertexShader, VkShaderModule FragmentShader,
        bool Wireframe, VkPipeline& Result)
    {
        std::array<VkPipelineShaderStageCreateInfo, 2> ShaderStages{};
        ShaderStages[0].sType =
            VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO;
        ShaderStages[0].stage = VK_SHADER_STAGE_VERTEX_BIT;
        ShaderStages[0].module = VertexShader;
        ShaderStages[0].pName = "main";
        ShaderStages[1].sType =
            VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO;
        ShaderStages[1].stage = VK_SHADER_STAGE_FRAGMENT_BIT;
        ShaderStages[1].module = FragmentShader;
        ShaderStages[1].pName = "main";

        VkVertexInputBindingDescription Binding{};
        Binding.binding = 0;
        Binding.stride = sizeof(GpuSceneVertex);
        Binding.inputRate = VK_VERTEX_INPUT_RATE_VERTEX;
        const std::array<VkVertexInputAttributeDescription, 8> Attributes
        {{
            {0, 0, VK_FORMAT_R32G32B32_SFLOAT,
                static_cast<uint32_t>(offsetof(GpuSceneVertex, Position))},
            {1, 0, VK_FORMAT_R32G32B32_SFLOAT,
                static_cast<uint32_t>(offsetof(GpuSceneVertex, Normal))},
            {2, 0, VK_FORMAT_R32G32B32A32_UINT,
                static_cast<uint32_t>(offsetof(GpuSceneVertex, BoneIndices))},
            {3, 0, VK_FORMAT_R32G32B32A32_SFLOAT,
                static_cast<uint32_t>(offsetof(GpuSceneVertex, BoneWeights))},
            {9, 0, VK_FORMAT_R32G32B32A32_UINT,
                static_cast<uint32_t>(offsetof(GpuSceneVertex, BoneIndices) + 4 * sizeof(uint32_t))},
            {10, 0, VK_FORMAT_R32G32B32A32_SFLOAT,
                static_cast<uint32_t>(offsetof(GpuSceneVertex, BoneWeights) + 4 * sizeof(float))},
            {11, 0, VK_FORMAT_R32G32B32A32_UINT,
                static_cast<uint32_t>(offsetof(GpuSceneVertex, BoneIndices) + 8 * sizeof(uint32_t))},
            {12, 0, VK_FORMAT_R32G32B32A32_SFLOAT,
                static_cast<uint32_t>(offsetof(GpuSceneVertex, BoneWeights) + 8 * sizeof(float))},
        }};
        VkPipelineVertexInputStateCreateInfo VertexInput{};
        VertexInput.sType = VK_STRUCTURE_TYPE_PIPELINE_VERTEX_INPUT_STATE_CREATE_INFO;
        VertexInput.vertexBindingDescriptionCount = 1;
        VertexInput.pVertexBindingDescriptions = &Binding;
        VertexInput.vertexAttributeDescriptionCount =
            static_cast<uint32_t>(Attributes.size());
        VertexInput.pVertexAttributeDescriptions = Attributes.data();

        VkPipelineInputAssemblyStateCreateInfo InputAssembly{};
        InputAssembly.sType =
            VK_STRUCTURE_TYPE_PIPELINE_INPUT_ASSEMBLY_STATE_CREATE_INFO;
        InputAssembly.topology = VK_PRIMITIVE_TOPOLOGY_TRIANGLE_LIST;
        VkPipelineViewportStateCreateInfo ViewportState{};
        ViewportState.sType = VK_STRUCTURE_TYPE_PIPELINE_VIEWPORT_STATE_CREATE_INFO;
        ViewportState.viewportCount = 1;
        ViewportState.scissorCount = 1;
        VkPipelineRasterizationStateCreateInfo Rasterization{};
        Rasterization.sType =
            VK_STRUCTURE_TYPE_PIPELINE_RASTERIZATION_STATE_CREATE_INFO;
        Rasterization.polygonMode = Wireframe ? VK_POLYGON_MODE_LINE :
            VK_POLYGON_MODE_FILL;
        Rasterization.cullMode = VK_CULL_MODE_NONE;
        Rasterization.frontFace = VK_FRONT_FACE_COUNTER_CLOCKWISE;
        Rasterization.lineWidth = Wireframe ? 1.5f : 1.0f;
        Rasterization.depthBiasEnable = Wireframe ? VK_TRUE : VK_FALSE;
        Rasterization.depthBiasConstantFactor = Wireframe ? -1.0f : 0.0f;
        Rasterization.depthBiasSlopeFactor = Wireframe ? -1.0f : 0.0f;
        VkPipelineMultisampleStateCreateInfo Multisample{};
        Multisample.sType =
            VK_STRUCTURE_TYPE_PIPELINE_MULTISAMPLE_STATE_CREATE_INFO;
        Multisample.rasterizationSamples = VK_SAMPLE_COUNT_1_BIT;
        VkPipelineDepthStencilStateCreateInfo DepthStencil{};
        DepthStencil.sType =
            VK_STRUCTURE_TYPE_PIPELINE_DEPTH_STENCIL_STATE_CREATE_INFO;
        DepthStencil.depthTestEnable = VK_TRUE;
        DepthStencil.depthWriteEnable = Wireframe ? VK_FALSE : VK_TRUE;
        DepthStencil.depthCompareOp = VK_COMPARE_OP_LESS_OR_EQUAL;
        VkPipelineColorBlendAttachmentState BlendAttachment{};
        BlendAttachment.colorWriteMask = VK_COLOR_COMPONENT_R_BIT |
            VK_COLOR_COMPONENT_G_BIT | VK_COLOR_COMPONENT_B_BIT |
            VK_COLOR_COMPONENT_A_BIT;
        VkPipelineColorBlendStateCreateInfo Blend{};
        Blend.sType = VK_STRUCTURE_TYPE_PIPELINE_COLOR_BLEND_STATE_CREATE_INFO;
        Blend.attachmentCount = 1;
        Blend.pAttachments = &BlendAttachment;
        const std::array<VkDynamicState, 2> DynamicStates
        {{
            VK_DYNAMIC_STATE_VIEWPORT,
            VK_DYNAMIC_STATE_SCISSOR,
        }};
        VkPipelineDynamicStateCreateInfo Dynamic{};
        Dynamic.sType = VK_STRUCTURE_TYPE_PIPELINE_DYNAMIC_STATE_CREATE_INFO;
        Dynamic.dynamicStateCount = static_cast<uint32_t>(DynamicStates.size());
        Dynamic.pDynamicStates = DynamicStates.data();

        VkGraphicsPipelineCreateInfo PipelineInfo{};
        PipelineInfo.sType = VK_STRUCTURE_TYPE_GRAPHICS_PIPELINE_CREATE_INFO;
        PipelineInfo.stageCount = static_cast<uint32_t>(ShaderStages.size());
        PipelineInfo.pStages = ShaderStages.data();
        PipelineInfo.pVertexInputState = &VertexInput;
        PipelineInfo.pInputAssemblyState = &InputAssembly;
        PipelineInfo.pViewportState = &ViewportState;
        PipelineInfo.pRasterizationState = &Rasterization;
        PipelineInfo.pMultisampleState = &Multisample;
        PipelineInfo.pDepthStencilState = &DepthStencil;
        PipelineInfo.pColorBlendState = &Blend;
        PipelineInfo.pDynamicState = &Dynamic;
        PipelineInfo.layout = PipelineLayout;
        PipelineInfo.renderPass = Context.render_pass;
        PipelineInfo.subpass = 0;
        return ResultSucceeded(vkCreateGraphicsPipelines(Device, VK_NULL_HANDLE,
            1, &PipelineInfo, nullptr, &Result),
            "scene graphics pipeline creation");
    }

    bool EnsurePipelines(const RmlUiVulkanFrameContext& Context)
    {
        const bool Current =
            PipelineGeneration == Context.render_pass_generation &&
            StaticPipeline && SkinnedPipeline && StaticSelectionPipeline &&
            SkinnedSelectionPipeline;
        if (Current)
        {
            return true;
        }
        DestroyPipelines();
        const VkShaderModule StaticShader = CreateShaderModule(Device,
            SceneStaticVert, sizeof(SceneStaticVert));
        const VkShaderModule SkinnedShader = CreateShaderModule(Device,
            SceneSkinnedVert, sizeof(SceneSkinnedVert));
        const VkShaderModule FragmentShader = CreateShaderModule(Device,
            SceneFrag, sizeof(SceneFrag));
        const bool ShadersReady = StaticShader && SkinnedShader && FragmentShader;
        const bool PipelinesReady = ShadersReady &&
            CreatePipeline(Context, StaticShader, FragmentShader, false,
                StaticPipeline) &&
            CreatePipeline(Context, SkinnedShader, FragmentShader, false,
                SkinnedPipeline) &&
            CreatePipeline(Context, StaticShader, FragmentShader, true,
                StaticSelectionPipeline) &&
            CreatePipeline(Context, SkinnedShader, FragmentShader, true,
                SkinnedSelectionPipeline);
        if (StaticShader)
        {
            vkDestroyShaderModule(Device, StaticShader, nullptr);
        }
        if (SkinnedShader)
        {
            vkDestroyShaderModule(Device, SkinnedShader, nullptr);
        }
        if (FragmentShader)
        {
            vkDestroyShaderModule(Device, FragmentShader, nullptr);
        }
        if (!PipelinesReady)
        {
            DestroyPipelines();
            EngineLog::Error("RenderCore", "Vulkan scene pipeline creation failed");
            return false;
        }
        PipelineGeneration = Context.render_pass_generation;
        return true;
    }

    bool UpdateFrameResources(const RmlUiVulkanFrameContext& Context,
        const GpuSceneFrame& Frame, const std::vector<InstanceData>& Instances)
    {
        FrameResources& Resources = Frames[Context.frame_slot % FrameSlotCount];
        const VkDeviceSize BoneBytes = (std::max)(VkDeviceSize(64),
            static_cast<VkDeviceSize>(Frame.SkinMatrixCount) * sizeof(float) * 16);
        const VkDeviceSize InstanceBytes = (std::max)(
            VkDeviceSize(sizeof(InstanceData)),
            static_cast<VkDeviceSize>(Instances.size()) * sizeof(InstanceData));
        const bool BuffersReady = EnsureBuffer(Context, sizeof(FrameData),
                VK_BUFFER_USAGE_UNIFORM_BUFFER_BIT, Resources.FrameBuffer) &&
            EnsureBuffer(Context, BoneBytes, VK_BUFFER_USAGE_STORAGE_BUFFER_BIT,
                Resources.BoneBuffer) &&
            EnsureBuffer(Context, InstanceBytes,
                VK_BUFFER_USAGE_STORAGE_BUFFER_BIT, Resources.InstanceBuffer);
        if (!BuffersReady)
        {
            return false;
        }

        FrameData Data{};
        std::memcpy(Data.ViewProjection, Frame.ViewProjection,
            sizeof(Data.ViewProjection));
        std::memcpy(Data.SkyRadiance, Frame.SkyRadiance,
            sizeof(Frame.SkyRadiance));
        const uint32_t LightCount = (std::min)(Frame.LightCount, MaximumLights);
        for (uint32_t Index = 0; Index < LightCount; ++Index)
        {
            std::memcpy(Data.LightDirections[Index], Frame.Lights[Index].Direction,
                sizeof(float) * 3);
            std::memcpy(Data.LightRadiances[Index], Frame.Lights[Index].Radiance,
                sizeof(float) * 3);
        }
        Data.Counts[0] = static_cast<int32_t>(LightCount);
        std::memcpy(Resources.FrameBuffer.Mapped, &Data, sizeof(Data));
        if (Frame.SkinMatrixCount > 0)
        {
            std::memcpy(Resources.BoneBuffer.Mapped, Frame.SkinMatrices,
                static_cast<size_t>(Frame.SkinMatrixCount) * sizeof(float) * 16);
        }
        if (!Instances.empty())
        {
            std::memcpy(Resources.InstanceBuffer.Mapped, Instances.data(),
                Instances.size() * sizeof(InstanceData));
        }

        const std::array<VkDescriptorBufferInfo, 3> BufferInfos
        {{
            {Resources.FrameBuffer.Handle, 0, sizeof(FrameData)},
            {Resources.BoneBuffer.Handle, 0, BoneBytes},
            {Resources.InstanceBuffer.Handle, 0, InstanceBytes},
        }};
        std::array<VkWriteDescriptorSet, 3> Writes{};
        for (uint32_t Index = 0; Index < Writes.size(); ++Index)
        {
            Writes[Index].sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET;
            Writes[Index].dstSet = Resources.DescriptorSet;
            Writes[Index].dstBinding = Index;
            Writes[Index].descriptorCount = 1;
            Writes[Index].descriptorType = Index == 0 ?
                VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER :
                VK_DESCRIPTOR_TYPE_STORAGE_BUFFER;
            Writes[Index].pBufferInfo = &BufferInfos[Index];
        }
        vkUpdateDescriptorSets(Device, static_cast<uint32_t>(Writes.size()),
            Writes.data(), 0, nullptr);
        return true;
    }

    void DestroyMesh(Mesh& Value)
    {
        DestroyBuffer(Device, Value.VertexBuffer);
        Value = {};
    }
};

GpuSceneRenderer_Vulkan::GpuSceneRenderer_Vulkan() :
    State(std::make_unique<Impl>())
{
}

GpuSceneRenderer_Vulkan::~GpuSceneRenderer_Vulkan()
{
    Shutdown();
}

bool GpuSceneRenderer_Vulkan::EnsureMesh(
    const RmlUiVulkanFrameContext& Context, const GpuMeshUpload* Upload)
{
    if (!Upload || !Upload->Vertices || Upload->VertexCount == 0 ||
        !State->EnsureDevice(Context))
    {
        return false;
    }
    if (State->Meshes.contains(Upload->Resource))
    {
        return true;
    }
    Impl::Mesh Mesh{};
    const VkDeviceSize Size =
        static_cast<VkDeviceSize>(Upload->VertexCount) * sizeof(GpuSceneVertex);
    if (!CreateBuffer(Context, Size, VK_BUFFER_USAGE_VERTEX_BUFFER_BIT,
        Mesh.VertexBuffer))
    {
        return false;
    }
    std::memcpy(Mesh.VertexBuffer.Mapped, Upload->Vertices,
        static_cast<size_t>(Size));
    Mesh.VertexCount = Upload->VertexCount;
    Mesh.Skinned = Upload->Skinned != 0;
    State->Meshes.emplace(Upload->Resource, std::move(Mesh));
    return true;
}

bool GpuSceneRenderer_Vulkan::Render(
    const RmlUiVulkanFrameContext& Context, const GpuSceneFrame* Frame)
{
    if (!Frame || !Context.command_buffer || !State->EnsureDevice(Context) ||
        !State->EnsurePipelines(Context))
    {
        return false;
    }
    for (uint32_t Index = 0; Index < Frame->DrawCount; ++Index)
    {
        const GpuSceneDraw& Draw = Frame->Draws[Index];
        if (!State->Meshes.contains(Draw.Resource) ||
            Draw.SkinMatrixOffset + Draw.SkinMatrixCount >
                Frame->SkinMatrixCount)
        {
            return false;
        }
    }

    std::vector<StaticBatch> StaticBatches;
    std::unordered_map<GpuResourceId, size_t, ResourceHash> StaticBatchIndices;
    std::vector<DrawRecord> SkinnedRecords;
    for (uint32_t Index = 0; Index < Frame->DrawCount; ++Index)
    {
        const GpuSceneDraw& Draw = Frame->Draws[Index];
        const auto& Mesh = State->Meshes.at(Draw.Resource);
        if (Mesh.Skinned)
        {
            SkinnedRecords.push_back({Draw.Resource, &Draw, 0, true});
            continue;
        }
        auto Iterator = StaticBatchIndices.find(Draw.Resource);
        if (Iterator == StaticBatchIndices.end())
        {
            const size_t BatchIndex = StaticBatches.size();
            StaticBatchIndices.emplace(Draw.Resource, BatchIndex);
            StaticBatches.push_back({Draw.Resource});
            Iterator = StaticBatchIndices.find(Draw.Resource);
        }
        StaticBatches[Iterator->second].Draws.push_back(&Draw);
    }

    std::vector<InstanceData> Instances;
    Instances.reserve(Frame->DrawCount);
    std::vector<DrawRecord> DrawRecords;
    DrawRecords.reserve(Frame->DrawCount);
    const auto AppendInstance = [&](const GpuSceneDraw& Draw,
        const GpuResourceId& Resource, bool Skinned)
    {
        InstanceData Instance{};
        std::memcpy(Instance.Model, Draw.Model, sizeof(Instance.Model));
        std::memcpy(Instance.Color, Draw.Color, sizeof(Instance.Color));
        Instance.BoneOffset = Draw.SkinMatrixOffset;
        Instance.BoneCount = Draw.SkinMatrixCount;
        Instance.Flags = Draw.Flags;
        const uint32_t InstanceIndex = static_cast<uint32_t>(Instances.size());
        Instances.push_back(Instance);
        DrawRecords.push_back({Resource, &Draw, InstanceIndex, Skinned});
    };
    for (StaticBatch& Batch : StaticBatches)
    {
        Batch.FirstInstance = static_cast<uint32_t>(Instances.size());
        for (const GpuSceneDraw* Draw : Batch.Draws)
        {
            AppendInstance(*Draw, Batch.Resource, false);
        }
    }
    for (DrawRecord& Record : SkinnedRecords)
    {
        AppendInstance(*Record.Draw, Record.Resource, true);
    }
    if (!State->UpdateFrameResources(Context, *Frame, Instances))
    {
        return false;
    }

    ++State->FrameNumber;
    for (DrawRecord& Record : DrawRecords)
    {
        State->Meshes.at(Record.Resource).LastUsedFrame = State->FrameNumber;
    }

    const int32_t ViewportX = static_cast<int32_t>(Frame->ViewportX);
    const int32_t ViewportY = static_cast<int32_t>(Frame->ViewportY);
    const uint32_t ViewportWidth = static_cast<uint32_t>(Frame->ViewportWidth);
    const uint32_t ViewportHeight = static_cast<uint32_t>(Frame->ViewportHeight);
    const bool ViewportValid = ViewportX >= 0 && ViewportY >= 0 &&
        ViewportWidth > 0 && ViewportHeight > 0 &&
        static_cast<uint32_t>(ViewportX) + ViewportWidth <= Context.extent.width &&
        static_cast<uint32_t>(ViewportY) + ViewportHeight <= Context.extent.height;
    if (!ViewportValid)
    {
        return false;
    }

    std::array<VkClearAttachment, 2> ClearAttachments{};
    ClearAttachments[0].aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    ClearAttachments[0].colorAttachment = 0;
    ClearAttachments[0].clearValue.color.float32[0] = 0.055f;
    ClearAttachments[0].clearValue.color.float32[1] = 0.065f;
    ClearAttachments[0].clearValue.color.float32[2] = 0.08f;
    ClearAttachments[0].clearValue.color.float32[3] = 1.0f;
    ClearAttachments[1].aspectMask = VK_IMAGE_ASPECT_DEPTH_BIT;
    ClearAttachments[1].clearValue.depthStencil = {1.0f, 0};
    VkClearRect ClearRect{};
    ClearRect.rect.offset = {ViewportX, ViewportY};
    ClearRect.rect.extent = {ViewportWidth, ViewportHeight};
    ClearRect.layerCount = 1;
    vkCmdClearAttachments(Context.command_buffer,
        static_cast<uint32_t>(ClearAttachments.size()), ClearAttachments.data(),
        1, &ClearRect);

    VkViewport Viewport{};
    Viewport.x = Frame->ViewportX;
    Viewport.y = Frame->ViewportY;
    Viewport.width = Frame->ViewportWidth;
    Viewport.height = Frame->ViewportHeight;
    Viewport.minDepth = 0.0f;
    Viewport.maxDepth = 1.0f;
    const VkRect2D Scissor{{ViewportX, ViewportY},
        {ViewportWidth, ViewportHeight}};
    vkCmdSetViewport(Context.command_buffer, 0, 1, &Viewport);
    vkCmdSetScissor(Context.command_buffer, 0, 1, &Scissor);

    const Impl::FrameResources& Resources =
        State->Frames[Context.frame_slot % FrameSlotCount];
    vkCmdBindDescriptorSets(Context.command_buffer,
        VK_PIPELINE_BIND_POINT_GRAPHICS, State->PipelineLayout, 0, 1,
        &Resources.DescriptorSet, 0, nullptr);
    const uint32_t FillPass = 0;
    vkCmdPushConstants(Context.command_buffer, State->PipelineLayout,
        VK_SHADER_STAGE_FRAGMENT_BIT, 0, sizeof(FillPass), &FillPass);
    vkCmdBindPipeline(Context.command_buffer, VK_PIPELINE_BIND_POINT_GRAPHICS,
        State->StaticPipeline);
    for (const StaticBatch& Batch : StaticBatches)
    {
        const auto& Mesh = State->Meshes.at(Batch.Resource);
        const VkDeviceSize Offset = 0;
        vkCmdBindVertexBuffers(Context.command_buffer, 0, 1,
            &Mesh.VertexBuffer.Handle, &Offset);
        vkCmdDraw(Context.command_buffer, Mesh.VertexCount,
            static_cast<uint32_t>(Batch.Draws.size()), 0, Batch.FirstInstance);
    }
    vkCmdBindPipeline(Context.command_buffer, VK_PIPELINE_BIND_POINT_GRAPHICS,
        State->SkinnedPipeline);
    for (const DrawRecord& Record : DrawRecords)
    {
        if (!Record.Skinned)
        {
            continue;
        }
        const auto& Mesh = State->Meshes.at(Record.Resource);
        const VkDeviceSize Offset = 0;
        vkCmdBindVertexBuffers(Context.command_buffer, 0, 1,
            &Mesh.VertexBuffer.Handle, &Offset);
        vkCmdDraw(Context.command_buffer, Mesh.VertexCount, 1, 0,
            Record.InstanceIndex);
    }

    const uint32_t SelectionPass = 1;
    vkCmdPushConstants(Context.command_buffer, State->PipelineLayout,
        VK_SHADER_STAGE_FRAGMENT_BIT, 0, sizeof(SelectionPass), &SelectionPass);
    for (const DrawRecord& Record : DrawRecords)
    {
        if ((Record.Draw->Flags & 1u) == 0)
        {
            continue;
        }
        const auto& Mesh = State->Meshes.at(Record.Resource);
        const VkPipeline Pipeline = Record.Skinned ?
            State->SkinnedSelectionPipeline : State->StaticSelectionPipeline;
        vkCmdBindPipeline(Context.command_buffer, VK_PIPELINE_BIND_POINT_GRAPHICS,
            Pipeline);
        const VkDeviceSize Offset = 0;
        vkCmdBindVertexBuffers(Context.command_buffer, 0, 1,
            &Mesh.VertexBuffer.Handle, &Offset);
        vkCmdDraw(Context.command_buffer, Mesh.VertexCount, 1, 0,
            Record.InstanceIndex);
    }

    vkCmdSetViewport(Context.command_buffer, 0, 1, &Context.viewport);
    vkCmdSetScissor(Context.command_buffer, 0, 1, &Context.scissor);
    for (auto Iterator = State->Meshes.begin(); Iterator != State->Meshes.end();)
    {
        if (State->FrameNumber > Iterator->second.LastUsedFrame + FrameSlotCount)
        {
            State->DestroyMesh(Iterator->second);
            Iterator = State->Meshes.erase(Iterator);
        }
        else
        {
            ++Iterator;
        }
    }
    return true;
}

void GpuSceneRenderer_Vulkan::Shutdown()
{
    if (!State || !State->Device)
    {
        return;
    }
    ResultSucceeded(vkDeviceWaitIdle(State->Device),
        "scene renderer shutdown");
    for (auto& Pair : State->Meshes)
    {
        State->DestroyMesh(Pair.second);
    }
    State->Meshes.clear();
    for (auto& Frame : State->Frames)
    {
        DestroyBuffer(State->Device, Frame.FrameBuffer);
        DestroyBuffer(State->Device, Frame.BoneBuffer);
        DestroyBuffer(State->Device, Frame.InstanceBuffer);
    }
    State->DestroyPipelines();
    if (State->DescriptorPool)
    {
        vkDestroyDescriptorPool(State->Device, State->DescriptorPool, nullptr);
    }
    if (State->PipelineLayout)
    {
        vkDestroyPipelineLayout(State->Device, State->PipelineLayout, nullptr);
    }
    if (State->DescriptorSetLayout)
    {
        vkDestroyDescriptorSetLayout(State->Device, State->DescriptorSetLayout,
            nullptr);
    }
    State = std::make_unique<Impl>();
}

static_assert(sizeof(FrameData) == 608);
static_assert(sizeof(InstanceData) == 96);
