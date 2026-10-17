#pragma once

#include <vulkan/vulkan.h>
#include <vector>
#include <string>
#include "VkSurface.h"

// 前向声明（避免头文件污染）
struct RenderInitDesc;

/**
 * VkContext
 * --------------------------------------
 * Vulkan 全局上下文：
 * - VkInstance
 * - Validation Layer
 * - Physical / Logical Device
 * - Graphics / Present Queue
 *
 * 生命周期：
 *   Initialize()
 *     -> Instance
 *     -> Debug
 *     -> Device
 *
 *   Shutdown()
 *     -> Device
 *     -> Debug
 *     -> Instance
 */
class VkContext
{
public:
    VkContext() = default;
    ~VkContext() = default;

    VkContext(const VkContext&) = delete;
    VkContext& operator=(const VkContext&) = delete;

public:
    bool Initialize(const RenderInitDesc* desc);
    void Shutdown();

public:
    // === Accessors（RenderBackend 内部使用） ===
    VkInstance       GetInstance() const        { return Instance; }
    VkPhysicalDevice GetPhysicalDevice() const  { return PhysicalDevice; }
    VkDevice         GetDevice() const          { return Device; }
    VkSurfaceKHR     GetSurface() const        { return Surface.Get(); }
    const VkSurface&  GetSurfaceObject() const  { return Surface; }

    uint32_t GetGraphicsQueueFamily() const { return GraphicsQueueFamily; }
    VkQueue  GetGraphicsQueue() const       { return GraphicsQueue; }

    uint32_t GetPresentQueueFamily() const  { return PresentQueueFamily; }
    VkQueue  GetPresentQueue() const        { return PresentQueue; }

    bool IsValidationEnabled() const { return bEnableValidation; }

private:
    // === Instance / Validation ===
    bool CreateInstance(const RenderInitDesc* desc);
    bool CreateSurface(const RenderInitDesc* desc);
    bool SetupDebugMessenger();
    void DestroyDebugMessenger();

    // === Device ===
    bool PickPhysicalDevice();
    bool CreateLogicalDevice();

private:
    // ----------------------------
    // Vulkan Handles
    // ----------------------------
    VkInstance Instance = VK_NULL_HANDLE;

    VkPhysicalDevice PhysicalDevice = VK_NULL_HANDLE;
    VkDevice Device = VK_NULL_HANDLE;
    VkSurface Surface;

    VkQueue GraphicsQueue = VK_NULL_HANDLE;
    VkQueue PresentQueue  = VK_NULL_HANDLE;

    uint32_t GraphicsQueueFamily = UINT32_MAX;
    uint32_t PresentQueueFamily  = UINT32_MAX;

    // ----------------------------
    // Debug / Validation
    // ----------------------------
    bool bEnableValidation = false;
    VkDebugUtilsMessengerEXT DebugMessenger = VK_NULL_HANDLE;

    // ----------------------------
    // Cached data
    // ----------------------------
    std::vector<const char*> EnabledLayers;
    std::vector<const char*> EnabledExtensions;
};
