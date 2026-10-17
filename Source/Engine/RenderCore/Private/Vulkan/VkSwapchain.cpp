#include "VkSwapchain.h"
#include "VkContext.h"
#include "VkSurface.h"

#include <limits>

bool VkSwapchain::Create(VkContext* ctx, VkSurface* surface, uint32_t width, uint32_t height)
{
    // A Win32 window's outer size is larger than its client area. Query the
    // surface before creating the swapchain so the native host never submits
    // an extent that the WSI implementation cannot present.
    VkSurfaceCapabilitiesKHR capabilities{};
    if (vkGetPhysicalDeviceSurfaceCapabilitiesKHR(
            ctx->GetPhysicalDevice(), surface->Get(), &capabilities) != VK_SUCCESS)
    {
        return false;
    }

    VkExtent2D requestedExtent{ width, height };
    VkExtent2D selectedExtent = requestedExtent;
    if (capabilities.currentExtent.width !=
            std::numeric_limits<uint32_t>::max())
    {
        selectedExtent = capabilities.currentExtent;
    }
    else
    {
        const uint32_t clampedWidth =
            requestedExtent.width < capabilities.minImageExtent.width
                ? capabilities.minImageExtent.width
                : requestedExtent.width > capabilities.maxImageExtent.width
                    ? capabilities.maxImageExtent.width
                    : requestedExtent.width;
        const uint32_t clampedHeight =
            requestedExtent.height < capabilities.minImageExtent.height
                ? capabilities.minImageExtent.height
                : requestedExtent.height > capabilities.maxImageExtent.height
                    ? capabilities.maxImageExtent.height
                    : requestedExtent.height;
        selectedExtent = { clampedWidth, clampedHeight };
    }

    if (selectedExtent.width == 0 || selectedExtent.height == 0)
    {
        return false;
    }

    VkSwapchainCreateInfoKHR info{};
    info.sType = VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR;
    info.surface = surface->Get();
    info.minImageCount = capabilities.minImageCount > 2
        ? capabilities.minImageCount
        : 2;
    if (capabilities.maxImageCount != 0 &&
        info.minImageCount > capabilities.maxImageCount)
    {
        info.minImageCount = capabilities.maxImageCount;
    }
    info.imageFormat = VK_FORMAT_B8G8R8A8_UNORM;
    info.imageColorSpace = VK_COLOR_SPACE_SRGB_NONLINEAR_KHR;
    info.imageExtent = selectedExtent;
    info.imageArrayLayers = 1;
    info.imageUsage = VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT;
    info.imageSharingMode = VK_SHARING_MODE_EXCLUSIVE;
    info.preTransform = VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR;
    info.compositeAlpha = VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR;
    info.presentMode = VK_PRESENT_MODE_FIFO_KHR;
    info.clipped = VK_TRUE;

    if (vkCreateSwapchainKHR(ctx->GetDevice(), &info, nullptr, &Swapchain) != VK_SUCCESS)
        return false;

    uint32_t count;
    vkGetSwapchainImagesKHR(ctx->GetDevice(), Swapchain, &count, nullptr);
    Images.resize(count);
    vkGetSwapchainImagesKHR(ctx->GetDevice(), Swapchain, &count, Images.data());

    ImageFormat = info.imageFormat;
    Extent = info.imageExtent;

    ImageViews.resize(count);
    for (uint32_t i = 0; i < count; ++i)
    {
        VkImageViewCreateInfo view{};
        view.sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO;
        view.image = Images[i];
        view.viewType = VK_IMAGE_VIEW_TYPE_2D;
        view.format = ImageFormat;
        view.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
        view.subresourceRange.levelCount = 1;
        view.subresourceRange.layerCount = 1;

        vkCreateImageView(ctx->GetDevice(), &view, nullptr, &ImageViews[i]);
    }

    return true;
}

void VkSwapchain::Destroy(VkContext* ctx)
{
    for (auto view : ImageViews)
        vkDestroyImageView(ctx->GetDevice(), view, nullptr);

    ImageViews.clear();

    if (Swapchain)
    {
        vkDestroySwapchainKHR(ctx->GetDevice(), Swapchain, nullptr);
        Swapchain = VK_NULL_HANDLE;
    }
}
