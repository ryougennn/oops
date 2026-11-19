using System.Runtime.InteropServices;

namespace oopshell.Native;

/// <summary>
/// The small part of <c>libvulkan.so.1</c> that oopshell needs to put Vulkan images on a Wayland surface. The
/// library is optional: it is only loaded when the Vulkan path is used; check <see cref="IsAvailable"/> first.
/// Everything is imported from the loader, which also exports the WSI (surface and swapchain) functions.
/// </summary>
public static unsafe partial class Vulkan
{
    private const string Lib = "libvulkan.so.1";

    public const int VK_SUCCESS = 0;
    public const int VK_SUBOPTIMAL_KHR = 1000001003;
    public const int VK_ERROR_OUT_OF_DATE_KHR = -1000001004;
    public const uint VK_API_VERSION_1_1 = (1 << 22) | (1 << 12);
    public const uint VK_QUEUE_GRAPHICS_BIT = 0x1;
    public const int VK_PHYSICAL_DEVICE_TYPE_CPU = 4;

    public const int VK_FORMAT_R8G8B8A8_UNORM = 37;
    public const int VK_FORMAT_B8G8R8A8_UNORM = 44;
    public const int VK_COLOR_SPACE_SRGB_NONLINEAR_KHR = 0;
    public const int VK_PRESENT_MODE_IMMEDIATE_KHR = 0;
    public const int VK_PRESENT_MODE_MAILBOX_KHR = 1;
    public const int VK_PRESENT_MODE_FIFO_KHR = 2;
    public const uint VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR = 0x1;
    public const uint VK_COMPOSITE_ALPHA_PRE_MULTIPLIED_BIT_KHR = 0x2;
    public const uint VK_COMPOSITE_ALPHA_INHERIT_BIT_KHR = 0x8;
    public const uint VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR = 0x1;

    public const uint VK_IMAGE_USAGE_TRANSFER_SRC_BIT = 0x1;
    public const uint VK_IMAGE_USAGE_TRANSFER_DST_BIT = 0x2;
    public const uint VK_IMAGE_USAGE_SAMPLED_BIT = 0x4;
    public const uint VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT = 0x10;
    public const int VK_IMAGE_TILING_OPTIMAL = 0;
    public const int VK_IMAGE_LAYOUT_UNDEFINED = 0;
    public const int VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL = 2;
    public const int VK_IMAGE_LAYOUT_PRESENT_SRC_KHR = 1000001002;
    public const uint VK_IMAGE_ASPECT_COLOR_BIT = 0x1;
    public const uint VK_ACCESS_COLOR_ATTACHMENT_READ_BIT = 0x80;
    public const uint VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT = 0x100;
    public const uint VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT = 0x1;
    public const uint VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT = 0x400;
    public const uint VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT = 0x2000;
    public const uint VK_QUEUE_FAMILY_IGNORED = ~0u;
    public const uint VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT = 0x2;
    public const uint VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT = 0x1;
    public const int VK_COMMAND_BUFFER_LEVEL_PRIMARY = 0;

    public const int VK_STRUCTURE_TYPE_APPLICATION_INFO = 0;
    public const int VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO = 1;
    public const int VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO = 2;
    public const int VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO = 3;
    public const int VK_STRUCTURE_TYPE_SUBMIT_INFO = 4;
    public const int VK_STRUCTURE_TYPE_FENCE_CREATE_INFO = 8;
    public const int VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO = 9;
    public const int VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO = 39;
    public const int VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO = 40;
    public const int VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO = 42;
    public const int VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER = 45;
    public const int VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR = 1000001000;
    public const int VK_STRUCTURE_TYPE_PRESENT_INFO_KHR = 1000001001;
    public const int VK_STRUCTURE_TYPE_WAYLAND_SURFACE_CREATE_INFO_KHR = 1000006000;

    /// <summary>Size of <c>VkPhysicalDeviceFeatures</c> (55 VkBool32s).</summary>
    public const int PhysicalDeviceFeaturesSize = 55 * 4;
    /// <summary>Byte offsets into <c>VkPhysicalDeviceProperties</c> (824 bytes on 64-bit).</summary>
    public const int PhysicalDevicePropertiesSize = 824, PropertiesApiVersionOffset = 0, PropertiesDeviceTypeOffset = 16, PropertiesDeviceNameOffset = 20;

    [StructLayout(LayoutKind.Sequential)]
    public struct VkApplicationInfo
    {
        public int sType;
        public nint pNext;
        public byte* pApplicationName;
        public uint applicationVersion;
        public byte* pEngineName;
        public uint engineVersion;
        public uint apiVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkInstanceCreateInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public VkApplicationInfo* pApplicationInfo;
        public uint enabledLayerCount;
        public byte** ppEnabledLayerNames;
        public uint enabledExtensionCount;
        public byte** ppEnabledExtensionNames;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkQueueFamilyProperties
    {
        public uint queueFlags;
        public uint queueCount;
        public uint timestampValidBits;
        public uint minImageTransferGranularityWidth, minImageTransferGranularityHeight, minImageTransferGranularityDepth;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkDeviceQueueCreateInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public uint queueFamilyIndex;
        public uint queueCount;
        public float* pQueuePriorities;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkDeviceCreateInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public uint queueCreateInfoCount;
        public VkDeviceQueueCreateInfo* pQueueCreateInfos;
        public uint enabledLayerCount;
        public byte** ppEnabledLayerNames;
        public uint enabledExtensionCount;
        public byte** ppEnabledExtensionNames;
        public void* pEnabledFeatures;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkWaylandSurfaceCreateInfoKHR
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public nint display;
        public nint surface;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkSurfaceCapabilitiesKHR
    {
        public uint minImageCount;
        public uint maxImageCount;
        public uint currentExtentWidth, currentExtentHeight;
        public uint minImageExtentWidth, minImageExtentHeight;
        public uint maxImageExtentWidth, maxImageExtentHeight;
        public uint maxImageArrayLayers;
        public uint supportedTransforms;
        public uint currentTransform;
        public uint supportedCompositeAlpha;
        public uint supportedUsageFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkSurfaceFormatKHR
    {
        public int format;
        public int colorSpace;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkSwapchainCreateInfoKHR
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public ulong surface;
        public uint minImageCount;
        public int imageFormat;
        public int imageColorSpace;
        public uint imageExtentWidth, imageExtentHeight;
        public uint imageArrayLayers;
        public uint imageUsage;
        public int imageSharingMode;
        public uint queueFamilyIndexCount;
        public uint* pQueueFamilyIndices;
        public uint preTransform;
        public uint compositeAlpha;
        public int presentMode;
        public uint clipped;
        public ulong oldSwapchain;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkPresentInfoKHR
    {
        public int sType;
        public nint pNext;
        public uint waitSemaphoreCount;
        public ulong* pWaitSemaphores;
        public uint swapchainCount;
        public ulong* pSwapchains;
        public uint* pImageIndices;
        public int* pResults;
    }

    /// <summary>Also used as <c>VkSemaphoreCreateInfo</c>, which has the same shape.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VkFenceCreateInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkCommandPoolCreateInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public uint queueFamilyIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkCommandBufferAllocateInfo
    {
        public int sType;
        public nint pNext;
        public ulong commandPool;
        public int level;
        public uint commandBufferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkCommandBufferBeginInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public nint pInheritanceInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkImageMemoryBarrier
    {
        public int sType;
        public nint pNext;
        public uint srcAccessMask;
        public uint dstAccessMask;
        public int oldLayout;
        public int newLayout;
        public uint srcQueueFamilyIndex;
        public uint dstQueueFamilyIndex;
        public ulong image;
        public uint aspectMask, baseMipLevel, levelCount, baseArrayLayer, layerCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkSubmitInfo
    {
        public int sType;
        public nint pNext;
        public uint waitSemaphoreCount;
        public ulong* pWaitSemaphores;
        public uint* pWaitDstStageMask;
        public uint commandBufferCount;
        public nint* pCommandBuffers;
        public uint signalSemaphoreCount;
        public ulong* pSignalSemaphores;
    }

    public static bool IsAvailable { get; } = NativeLibrary.TryLoad(Lib, out _);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint vkGetInstanceProcAddr(nint instance, string name);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint vkGetDeviceProcAddr(nint device, string name);
    [LibraryImport(Lib)] public static partial int vkEnumerateInstanceVersion(uint* apiVersion);
    [LibraryImport(Lib)] public static partial int vkCreateInstance(VkInstanceCreateInfo* info, nint allocator, nint* instance);
    [LibraryImport(Lib)] public static partial void vkDestroyInstance(nint instance, nint allocator);
    [LibraryImport(Lib)] public static partial int vkEnumeratePhysicalDevices(nint instance, uint* count, nint* devices);
    [LibraryImport(Lib)] public static partial void vkGetPhysicalDeviceProperties(nint physicalDevice, byte* properties);
    [LibraryImport(Lib)] public static partial void vkGetPhysicalDeviceQueueFamilyProperties(nint physicalDevice, uint* count, VkQueueFamilyProperties* properties);
    [LibraryImport(Lib)] public static partial uint vkGetPhysicalDeviceWaylandPresentationSupportKHR(nint physicalDevice, uint queueFamily, nint display);
    [LibraryImport(Lib)] public static partial int vkCreateDevice(nint physicalDevice, VkDeviceCreateInfo* info, nint allocator, nint* device);
    [LibraryImport(Lib)] public static partial void vkDestroyDevice(nint device, nint allocator);
    [LibraryImport(Lib)] public static partial void vkGetDeviceQueue(nint device, uint family, uint index, nint* queue);
    [LibraryImport(Lib)] public static partial int vkDeviceWaitIdle(nint device);

    [LibraryImport(Lib)] public static partial int vkCreateWaylandSurfaceKHR(nint instance, VkWaylandSurfaceCreateInfoKHR* info, nint allocator, ulong* surface);
    [LibraryImport(Lib)] public static partial void vkDestroySurfaceKHR(nint instance, ulong surface, nint allocator);
    [LibraryImport(Lib)] public static partial int vkGetPhysicalDeviceSurfaceSupportKHR(nint physicalDevice, uint family, ulong surface, uint* supported);
    [LibraryImport(Lib)] public static partial int vkGetPhysicalDeviceSurfaceCapabilitiesKHR(nint physicalDevice, ulong surface, VkSurfaceCapabilitiesKHR* capabilities);
    [LibraryImport(Lib)] public static partial int vkGetPhysicalDeviceSurfaceFormatsKHR(nint physicalDevice, ulong surface, uint* count, VkSurfaceFormatKHR* formats);
    [LibraryImport(Lib)] public static partial int vkGetPhysicalDeviceSurfacePresentModesKHR(nint physicalDevice, ulong surface, uint* count, int* modes);
    [LibraryImport(Lib)] public static partial int vkCreateSwapchainKHR(nint device, VkSwapchainCreateInfoKHR* info, nint allocator, ulong* swapchain);
    [LibraryImport(Lib)] public static partial void vkDestroySwapchainKHR(nint device, ulong swapchain, nint allocator);
    [LibraryImport(Lib)] public static partial int vkGetSwapchainImagesKHR(nint device, ulong swapchain, uint* count, ulong* images);
    [LibraryImport(Lib)] public static partial int vkAcquireNextImageKHR(nint device, ulong swapchain, ulong timeout, ulong semaphore, ulong fence, uint* index);
    [LibraryImport(Lib)] public static partial int vkQueuePresentKHR(nint queue, VkPresentInfoKHR* info);

    [LibraryImport(Lib)] public static partial int vkCreateFence(nint device, VkFenceCreateInfo* info, nint allocator, ulong* fence);
    [LibraryImport(Lib)] public static partial void vkDestroyFence(nint device, ulong fence, nint allocator);
    [LibraryImport(Lib)] public static partial int vkWaitForFences(nint device, uint count, ulong* fences, uint waitAll, ulong timeout);
    [LibraryImport(Lib)] public static partial int vkResetFences(nint device, uint count, ulong* fences);
    [LibraryImport(Lib)] public static partial int vkCreateSemaphore(nint device, VkFenceCreateInfo* info, nint allocator, ulong* semaphore);
    [LibraryImport(Lib)] public static partial void vkDestroySemaphore(nint device, ulong semaphore, nint allocator);
    [LibraryImport(Lib)] public static partial int vkCreateCommandPool(nint device, VkCommandPoolCreateInfo* info, nint allocator, ulong* pool);
    [LibraryImport(Lib)] public static partial void vkDestroyCommandPool(nint device, ulong pool, nint allocator);
    [LibraryImport(Lib)] public static partial int vkAllocateCommandBuffers(nint device, VkCommandBufferAllocateInfo* info, nint* buffers);
    [LibraryImport(Lib)] public static partial void vkFreeCommandBuffers(nint device, ulong pool, uint count, nint* buffers);
    [LibraryImport(Lib)] public static partial int vkBeginCommandBuffer(nint buffer, VkCommandBufferBeginInfo* info);
    [LibraryImport(Lib)] public static partial int vkEndCommandBuffer(nint buffer);
    [LibraryImport(Lib)]
    public static partial void vkCmdPipelineBarrier(nint buffer, uint srcStage, uint dstStage, uint dependency,
        uint memoryBarrierCount, void* memoryBarriers, uint bufferBarrierCount, void* bufferBarriers, uint imageBarrierCount, VkImageMemoryBarrier* imageBarriers);
    [LibraryImport(Lib)] public static partial int vkQueueSubmit(nint queue, uint count, VkSubmitInfo* submits, ulong fence);
}
