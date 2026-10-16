#pragma once
#include <vulkan/vulkan.h>
#include "Platform/PlatformTypes.h"

class VkSurface
{
public:
    // 接受平台层通用的 NativeWindowHandle，避免依赖 Win32Window*
    bool Create(VkInstance instance, const Platform::NativeWindowHandle& window);
    void Destroy(VkInstance instance);

    VkSurfaceKHR Get() const { return Surface; }

private:
    VkSurfaceKHR Surface = VK_NULL_HANDLE;
};
