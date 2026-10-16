#include "VkContext.h"

#include <cassert>
#include <cstring>
#include <iostream>

#if defined(_WIN32)
    #define VK_USE_PLATFORM_WIN32_KHR
    #include <windows.h>
    #include <vulkan/vulkan_win32.h>
#endif

static const char* GValidationLayers[] =
{
    "VK_LAYER_KHRONOS_validation"
};

#ifdef _DEBUG
    static constexpr bool GEnableValidationByDefault = true;
#else
    static constexpr bool GEnableValidationByDefault = false;
#endif

static VKAPI_ATTR VkBool32 VKAPI_CALL VulkanDebugCallback(
    VkDebugUtilsMessageSeverityFlagBitsEXT messageSeverity,
    VkDebugUtilsMessageTypeFlagsEXT messageType,
    const VkDebugUtilsMessengerCallbackDataEXT* callbackData,
    void* userData)
{
    std::cerr << "[Vulkan] " << callbackData->pMessage << std::endl;
    return VK_FALSE;
}

struct QueueFamilyIndices
{
    uint32_t Graphics = UINT32_MAX;
    uint32_t Present  = UINT32_MAX;

    bool IsComplete() const
    {
        return Graphics != UINT32_MAX && Present != UINT32_MAX;
    }
};

bool VkContext::Initialize(const RenderInitDesc* desc)
{
    bEnableValidation = GEnableValidationByDefault;

    if (!CreateInstance(desc))
        return false;

    if (bEnableValidation)
    {
        if (!SetupDebugMessenger())
            return false;
    }

    if (!PickPhysicalDevice())
        return false;

    if (!CreateLogicalDevice())
        return false;

    return true;
}

bool VkContext::CreateInstance(const RenderInitDesc* desc)
{
    // ----------------------------
    // Application Info
    // ----------------------------
    VkApplicationInfo appInfo{};
    appInfo.sType = VK_STRUCTURE_TYPE_APPLICATION_INFO;
    appInfo.pApplicationName = "SelfEngine";
    appInfo.applicationVersion = VK_MAKE_VERSION(1, 0, 0);
    appInfo.pEngineName = "SelfEngine";
    appInfo.engineVersion = VK_MAKE_VERSION(0, 1, 0);
    appInfo.apiVersion = VK_API_VERSION_1_2;

    EnabledExtensions.clear();

    // 基础扩展（Surface）
    EnabledExtensions.push_back(VK_KHR_SURFACE_EXTENSION_NAME);

#if defined(_WIN32)
    EnabledExtensions.push_back(VK_KHR_WIN32_SURFACE_EXTENSION_NAME);
#endif

    // Debug
    if (bEnableValidation)
    {
        EnabledExtensions.push_back(VK_EXT_DEBUG_UTILS_EXTENSION_NAME);
    }
    EnabledLayers.clear();

    if (bEnableValidation)
    {
        EnabledLayers.push_back(GValidationLayers[0]);
    }

    VkInstanceCreateInfo createInfo{};
    createInfo.sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO;
    createInfo.pApplicationInfo = &appInfo;
    createInfo.enabledExtensionCount = static_cast<uint32_t>(EnabledExtensions.size());
    createInfo.ppEnabledExtensionNames = EnabledExtensions.data();
    createInfo.enabledLayerCount = static_cast<uint32_t>(EnabledLayers.size());
    createInfo.ppEnabledLayerNames = EnabledLayers.data();

    VkResult result = vkCreateInstance(&createInfo, nullptr, &Instance);
    if (result != VK_SUCCESS)
    {
        std::cerr << "Failed to create Vulkan Instance!" << std::endl;
        return false;
    }

    return true;
}

bool VkContext::SetupDebugMessenger()
{
    VkDebugUtilsMessengerCreateInfoEXT createInfo{};
    createInfo.sType = VK_STRUCTURE_TYPE_DEBUG_UTILS_MESSENGER_CREATE_INFO_EXT;
    createInfo.messageSeverity =
        VK_DEBUG_UTILS_MESSAGE_SEVERITY_WARNING_BIT_EXT |
        VK_DEBUG_UTILS_MESSAGE_SEVERITY_ERROR_BIT_EXT;

    createInfo.messageType =
        VK_DEBUG_UTILS_MESSAGE_TYPE_GENERAL_BIT_EXT |
        VK_DEBUG_UTILS_MESSAGE_TYPE_VALIDATION_BIT_EXT |
        VK_DEBUG_UTILS_MESSAGE_TYPE_PERFORMANCE_BIT_EXT;

    createInfo.pfnUserCallback = VulkanDebugCallback;    
    
    auto func = (PFN_vkCreateDebugUtilsMessengerEXT)
        vkGetInstanceProcAddr(Instance, "vkCreateDebugUtilsMessengerEXT");

    if (!func)
    {
        std::cerr << "vkCreateDebugUtilsMessengerEXT not found!" << std::endl;
        return false;
    }

    if (func(Instance, &createInfo, nullptr, &DebugMessenger) != VK_SUCCESS)
    {
        std::cerr << "Failed to create Debug Messenger!" << std::endl;
        return false;
    }

    return true;
}

void VkContext::DestroyDebugMessenger()
{
    if (!DebugMessenger)
        return;

    auto func = (PFN_vkDestroyDebugUtilsMessengerEXT)
        vkGetInstanceProcAddr(Instance, "vkDestroyDebugUtilsMessengerEXT");

    if (func)
    {
        func(Instance, DebugMessenger, nullptr);
    }

    DebugMessenger = VK_NULL_HANDLE;
}

void VkContext::Shutdown()
{
    if (Device)
    {
        vkDeviceWaitIdle(Device);
        vkDestroyDevice(Device, nullptr);
        Device = VK_NULL_HANDLE;
    }
    
    if (bEnableValidation)
    {
        DestroyDebugMessenger();
    }

    if (Instance)
    {
        vkDestroyInstance(Instance, nullptr);
        Instance = VK_NULL_HANDLE;
    }
}

bool VkContext::PickPhysicalDevice()
{
    uint32_t deviceCount = 0;
    vkEnumeratePhysicalDevices(Instance, &deviceCount, nullptr);

    if (deviceCount == 0)
    {
        std::cerr << "No Vulkan physical devices found!" << std::endl;
        return false;
    }

    std::vector<VkPhysicalDevice> devices(deviceCount);
    vkEnumeratePhysicalDevices(Instance, &deviceCount, devices.data());

    for (VkPhysicalDevice device : devices)
    {
        QueueFamilyIndices indices{};

        // 查询 Queue Families
        uint32_t queueFamilyCount = 0;
        vkGetPhysicalDeviceQueueFamilyProperties(device, &queueFamilyCount, nullptr);

        std::vector<VkQueueFamilyProperties> families(queueFamilyCount);
        vkGetPhysicalDeviceQueueFamilyProperties(device, &queueFamilyCount, families.data());

        for (uint32_t i = 0; i < queueFamilyCount; ++i)
        {
            const VkQueueFamilyProperties& props = families[i];

            // Graphics Queue
            if (props.queueFlags & VK_QUEUE_GRAPHICS_BIT)
            {
                indices.Graphics = i;
            }

            // Present Queue（⚠️ 这里先假设支持，后面 swapchain 会细化）
            indices.Present = indices.Graphics;

            if (indices.IsComplete())
                break;
        }

        if (indices.IsComplete())
        {
            PhysicalDevice = device;
            GraphicsQueueFamily = indices.Graphics;
            PresentQueueFamily  = indices.Present;
            return true;
        }
    }

    std::cerr << "Failed to find suitable GPU!" << std::endl;
    return false;
}

bool VkContext::CreateLogicalDevice()
{
    std::vector<VkDeviceQueueCreateInfo> queueInfos;
    std::vector<uint32_t> uniqueFamilies;

    uniqueFamilies.push_back(GraphicsQueueFamily);
    if (PresentQueueFamily != GraphicsQueueFamily)
    {
        uniqueFamilies.push_back(PresentQueueFamily);
    }

    float queuePriority = 1.0f;

    for (uint32_t family : uniqueFamilies)
    {
        VkDeviceQueueCreateInfo queueInfo{};
        queueInfo.sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO;
        queueInfo.queueFamilyIndex = family;
        queueInfo.queueCount = 1;
        queueInfo.pQueuePriorities = &queuePriority;
        queueInfos.push_back(queueInfo);
    }

    // 设备特性（第一版先最小）
    VkPhysicalDeviceFeatures deviceFeatures{};

    std::vector<const char*> deviceExtensions =
    {
        VK_KHR_SWAPCHAIN_EXTENSION_NAME
    };

    VkDeviceCreateInfo createInfo{};
    createInfo.sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO;
    createInfo.queueCreateInfoCount = static_cast<uint32_t>(queueInfos.size());
    createInfo.pQueueCreateInfos = queueInfos.data();
    createInfo.pEnabledFeatures = &deviceFeatures;
    createInfo.enabledExtensionCount = static_cast<uint32_t>(deviceExtensions.size());
    createInfo.ppEnabledExtensionNames = deviceExtensions.data();

    if (bEnableValidation)
    {
        createInfo.enabledLayerCount = static_cast<uint32_t>(EnabledLayers.size());
        createInfo.ppEnabledLayerNames = EnabledLayers.data();
    }

    if (vkCreateDevice(PhysicalDevice, &createInfo, nullptr, &Device) != VK_SUCCESS)
    {
        std::cerr << "Failed to create logical device!" << std::endl;
        return false;
    }

    vkGetDeviceQueue(Device, GraphicsQueueFamily, 0, &GraphicsQueue);
    vkGetDeviceQueue(Device, PresentQueueFamily, 0, &PresentQueue);

    return true;
}

