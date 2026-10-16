#include "VkSurface.h"
#include <windows.h>
#include <vulkan/vulkan_win32.h>

bool VkSurface::Create(VkInstance instance, const Platform::NativeWindowHandle& window)
{
    VkWin32SurfaceCreateInfoKHR info{};
    info.sType = VK_STRUCTURE_TYPE_WIN32_SURFACE_CREATE_INFO_KHR;
    info.pNext = nullptr;
    info.flags = 0;
    info.hinstance = reinterpret_cast<HINSTANCE>(window.Instance);
    info.hwnd = reinterpret_cast<HWND>(window.Window);

    return vkCreateWin32SurfaceKHR(instance, &info, nullptr, &Surface) == VK_SUCCESS;
}

void VkSurface::Destroy(VkInstance instance)
{
    if (Surface)
    {
        vkDestroySurfaceKHR(instance, Surface, nullptr);
        Surface = VK_NULL_HANDLE;
    }
}
