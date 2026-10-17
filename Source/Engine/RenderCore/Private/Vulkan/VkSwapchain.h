#pragma once
#include <vulkan/vulkan.h>
#include <vector>

class VkContext;
class VkSurface;

class VkSwapchain
{
public:
    bool Create(VkContext* context, VkSurface* surface, uint32_t width, uint32_t height);
    void Destroy(VkContext* context);

    VkFormat GetFormat() const { return ImageFormat; }
    VkExtent2D GetExtent() const { return Extent; }
    const std::vector<VkImageView>& GetImageViews() const { return ImageViews; }

private:
    VkSwapchainKHR Swapchain = VK_NULL_HANDLE;
    VkFormat ImageFormat{};
    VkExtent2D Extent{};

    std::vector<VkImage> Images;
    std::vector<VkImageView> ImageViews;
};
