using System.Drawing;
using static oopshell.Native.Vulkan;
using static oopshell.Vulkan.VulkanDevice;

namespace oopshell.Vulkan;

/// <summary>
/// GPU rendering for a <see cref="Surface"/> with Vulkan: a <c>VkSurfaceKHR</c> and a swapchain on the app's shared
/// <see cref="VulkanDevice"/>. Each frame: <see cref="Acquire"/> an image, record and submit your rendering to
/// <see cref="VulkanDevice.Queue"/>, then <see cref="Present"/>.
/// <para>
/// Between the two calls the image is ready to draw (the acquire is waited for on the CPU) and in
/// <c>VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL</c>; leave it in that layout. That makes the window usable by
/// libraries that cannot wait on or signal semaphores, such as SkiaSharp. The present mode is mailbox (or
/// immediate) where available: pacing comes from oopshell's frame callbacks, as with EGL.
/// </para>
/// </summary>
public sealed unsafe class VulkanWindow : IDisposable
{
    // No INPUT_ATTACHMENT: with it, SkiaSharp's Vulkan backend (3.119 and 4.157) crashes on blend modes that read the
    // destination (e.g. Multiply) when drawing into a wrapped image; without it Skia copies the destination instead.
    const uint DesiredUsage = VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT
        | VK_IMAGE_USAGE_TRANSFER_DST_BIT | VK_IMAGE_USAGE_SAMPLED_BIT;

    sealed class Slot
    {
        public ulong Image;
        public nint Before, After; // command buffers with the layout transitions
        public ulong RenderDone;   // semaphore the present waits on
        public ulong Done;         // fence: this slot's last submission finished
        public bool Submitted, Presented;
    }

    readonly Surface surface;
    readonly ulong vkSurface;
    readonly ulong commandPool;
    readonly ulong acquireFence;
    readonly int presentMode;
    ulong swapchain;
    Slot[] slots = [];
    Size size;
    int current = -1;
    bool outOfDate;
    bool disposed;

    VulkanWindow(Surface surface, VulkanDevice device)
    {
        this.surface = surface;
        Device = device;
        var surfaceInfo = new VkWaylandSurfaceCreateInfoKHR
        {
            sType = VK_STRUCTURE_TYPE_WAYLAND_SURFACE_CREATE_INFO_KHR,
            display = surface.App.Display.Handle,
            surface = surface.NativeSurface,
        };
        ulong s;
        Check(vkCreateWaylandSurfaceKHR(device.Instance, &surfaceInfo, 0, &s), "vkCreateWaylandSurfaceKHR");
        vkSurface = s;

        uint supported;
        Check(vkGetPhysicalDeviceSurfaceSupportKHR(device.PhysicalDevice, device.QueueFamily, vkSurface, &supported), "vkGetPhysicalDeviceSurfaceSupportKHR");
        (Format, ColorSpace) = PickFormat();
        if (supported == 0 || Format == 0)
        {
            vkDestroySurfaceKHR(device.Instance, vkSurface, 0);
            throw new NotSupportedException($"{device.DeviceName} cannot present to this surface.");
        }
        presentMode = PickPresentMode();

        var poolInfo = new VkCommandPoolCreateInfo
        {
            sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO,
            flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT,
            queueFamilyIndex = device.QueueFamily,
        };
        ulong pool;
        Check(vkCreateCommandPool(device.Device, &poolInfo, 0, &pool), "vkCreateCommandPool");
        commandPool = pool;
        acquireFence = CreateFence(signaled: false);
    }

    /// <summary>Creates a Vulkan window for <paramref name="surface"/>. Throws <see cref="NotSupportedException"/> without Vulkan.</summary>
    public static VulkanWindow Create(Surface surface) => new(surface, VulkanDevice.Get(surface.App));

    public VulkanDevice Device { get; }
    /// <summary><c>VkFormat</c> of the swapchain images: B8G8R8A8_UNORM or R8G8B8A8_UNORM.</summary>
    public int Format { get; }
    public int ColorSpace { get; }
    /// <summary><c>VkImageUsageFlags</c> the swapchain images were created with.</summary>
    public uint ImageUsage { get; private set; }
    public Size Size => size;
    /// <summary>The image <see cref="Acquire"/> returned, until <see cref="Present"/>.</summary>
    public ulong CurrentImage => current >= 0 ? slots[current].Image : 0;

    /// <summary>Raised before the swapchain images are destroyed (resize or dispose): drop anything wrapping them.</summary>
    public event Action? ImagesDestroying;

    /// <summary>
    /// Acquires the next image at <paramref name="bufferSize"/> (recreating the swapchain when the size changed)
    /// and returns its index; the image is <c>CurrentImage</c>.
    /// </summary>
    public int Acquire(Size bufferSize)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (current >= 0)
            throw new InvalidOperationException("Present was not called for the previous image.");
        if (swapchain == 0 || outOfDate || bufferSize != size)
            CreateSwapchain(bufferSize);

        uint index;
        int result = vkAcquireNextImageKHR(Device.Device, swapchain, ulong.MaxValue, 0, acquireFence, &index);
        if (result == VK_ERROR_OUT_OF_DATE_KHR)
        {
            CreateSwapchain(bufferSize);
            result = vkAcquireNextImageKHR(Device.Device, swapchain, ulong.MaxValue, 0, acquireFence, &index);
        }
        if (result != VK_SUBOPTIMAL_KHR)
            Check(result, "vkAcquireNextImageKHR");
        WaitAndReset(acquireFence);

        var slot = slots[index];
        if (slot.Submitted)
            WaitAndReset(slot.Done);
        slot.Submitted = false;
        Record(slot.Before, slot.Image, slot.Presented ? VK_IMAGE_LAYOUT_PRESENT_SRC_KHR : VK_IMAGE_LAYOUT_UNDEFINED,
            VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL, 0, VK_ACCESS_COLOR_ATTACHMENT_READ_BIT | VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT,
            VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT);
        Submit(slot.Before, signal: 0, fence: 0);
        current = (int)index;
        return current;
    }

    /// <summary>The swapchain image at <paramref name="index"/> (stable until <see cref="ImagesDestroying"/>).</summary>
    public ulong GetImage(int index) => slots[index].Image;

    /// <summary>Presents the acquired image. Submit all rendering to <see cref="VulkanDevice.Queue"/> first.</summary>
    public void Present()
    {
        if (current < 0)
            throw new InvalidOperationException("Acquire was not called.");
        var slot = slots[current];
        Record(slot.After, slot.Image, VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL, VK_IMAGE_LAYOUT_PRESENT_SRC_KHR,
            VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT, 0, VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT, VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT);
        Submit(slot.After, slot.RenderDone, slot.Done);
        slot.Submitted = true;
        slot.Presented = true;

        surface.PrepareCommit();
        ulong semaphore = slot.RenderDone, chain = swapchain;
        uint index = (uint)current;
        // shortcut: no VK_KHR_incremental_present, so the compositor is told the whole buffer changed;
        // add it if compositor-side damage ever matters for Vulkan windows.
        var info = new VkPresentInfoKHR
        {
            sType = VK_STRUCTURE_TYPE_PRESENT_INFO_KHR,
            waitSemaphoreCount = 1,
            pWaitSemaphores = &semaphore,
            swapchainCount = 1,
            pSwapchains = &chain,
            pImageIndices = &index,
        };
        current = -1;
        int result = vkQueuePresentKHR(Device.Queue, &info);
        if (result is VK_SUBOPTIMAL_KHR or VK_ERROR_OUT_OF_DATE_KHR)
            outOfDate = true;
        else
            Check(result, "vkQueuePresentKHR");
    }

    (int Format, int ColorSpace) PickFormat()
    {
        uint count;
        Check(vkGetPhysicalDeviceSurfaceFormatsKHR(Device.PhysicalDevice, vkSurface, &count, null), "vkGetPhysicalDeviceSurfaceFormatsKHR");
        var formats = new VkSurfaceFormatKHR[count];
        fixed (VkSurfaceFormatKHR* p = formats)
            Check(vkGetPhysicalDeviceSurfaceFormatsKHR(Device.PhysicalDevice, vkSurface, &count, p), "vkGetPhysicalDeviceSurfaceFormatsKHR");
        foreach (int wanted in (int[])[VK_FORMAT_B8G8R8A8_UNORM, VK_FORMAT_R8G8B8A8_UNORM])
        {
            foreach (var f in formats)
            {
                if (f.format == wanted && f.colorSpace == VK_COLOR_SPACE_SRGB_NONLINEAR_KHR)
                    return (f.format, f.colorSpace);
            }
        }
        return (0, 0);
    }

    int PickPresentMode()
    {
        uint count;
        Check(vkGetPhysicalDeviceSurfacePresentModesKHR(Device.PhysicalDevice, vkSurface, &count, null), "vkGetPhysicalDeviceSurfacePresentModesKHR");
        var modes = new int[count];
        fixed (int* p = modes)
            Check(vkGetPhysicalDeviceSurfacePresentModesKHR(Device.PhysicalDevice, vkSurface, &count, p), "vkGetPhysicalDeviceSurfacePresentModesKHR");
        // FIFO would make Mesa wait for its own frame callbacks, blocking the event loop while the surface is hidden.
        return modes.Contains(VK_PRESENT_MODE_MAILBOX_KHR) ? VK_PRESENT_MODE_MAILBOX_KHR
            : modes.Contains(VK_PRESENT_MODE_IMMEDIATE_KHR) ? VK_PRESENT_MODE_IMMEDIATE_KHR
            : VK_PRESENT_MODE_FIFO_KHR;
    }

    void CreateSwapchain(Size bufferSize)
    {
        if (bufferSize.IsEmpty)
            throw new ArgumentException("The buffer size is empty.", nameof(bufferSize));
        VkSurfaceCapabilitiesKHR caps;
        Check(vkGetPhysicalDeviceSurfaceCapabilitiesKHR(Device.PhysicalDevice, vkSurface, &caps), "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");
        uint imageCount = Math.Max(caps.minImageCount, 3);
        if (caps.maxImageCount != 0)
            imageCount = Math.Min(imageCount, caps.maxImageCount);
        uint alpha = (caps.supportedCompositeAlpha & VK_COMPOSITE_ALPHA_PRE_MULTIPLIED_BIT_KHR) != 0 ? VK_COMPOSITE_ALPHA_PRE_MULTIPLIED_BIT_KHR
            : (caps.supportedCompositeAlpha & VK_COMPOSITE_ALPHA_INHERIT_BIT_KHR) != 0 ? VK_COMPOSITE_ALPHA_INHERIT_BIT_KHR
            : VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR;
        ImageUsage = DesiredUsage & caps.supportedUsageFlags;

        var info = new VkSwapchainCreateInfoKHR
        {
            sType = VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR,
            surface = vkSurface,
            minImageCount = imageCount,
            imageFormat = Format,
            imageColorSpace = ColorSpace,
            imageExtentWidth = (uint)bufferSize.Width,
            imageExtentHeight = (uint)bufferSize.Height,
            imageArrayLayers = 1,
            imageUsage = ImageUsage,
            preTransform = VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR,
            compositeAlpha = alpha,
            presentMode = presentMode,
            clipped = 1,
            oldSwapchain = swapchain,
        };
        ulong created;
        Check(vkCreateSwapchainKHR(Device.Device, &info, 0, &created), "vkCreateSwapchainKHR");
        DestroySlots();
        swapchain = created;
        size = bufferSize;
        outOfDate = false;

        uint count;
        Check(vkGetSwapchainImagesKHR(Device.Device, swapchain, &count, null), "vkGetSwapchainImagesKHR");
        var images = new ulong[count];
        fixed (ulong* p = images)
            Check(vkGetSwapchainImagesKHR(Device.Device, swapchain, &count, p), "vkGetSwapchainImagesKHR");
        var buffers = new nint[count * 2];
        var allocInfo = new VkCommandBufferAllocateInfo
        {
            sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO,
            commandPool = commandPool,
            level = VK_COMMAND_BUFFER_LEVEL_PRIMARY,
            commandBufferCount = count * 2,
        };
        fixed (nint* p = buffers)
            Check(vkAllocateCommandBuffers(Device.Device, &allocInfo, p), "vkAllocateCommandBuffers");
        slots = new Slot[count];
        for (int i = 0; i < count; i++)
        {
            slots[i] = new Slot
            {
                Image = images[i],
                Before = buffers[2 * i],
                After = buffers[2 * i + 1],
                RenderDone = CreateSemaphore(),
                Done = CreateFence(signaled: false),
            };
        }
    }

    /// <summary>Destroys the old swapchain's images (after <see cref="ImagesDestroying"/>), keeping the swapchain handle for reuse as oldSwapchain.</summary>
    void DestroySlots()
    {
        if (slots.Length == 0 && swapchain == 0)
            return;
        ImagesDestroying?.Invoke();
        vkDeviceWaitIdle(Device.Device);
        var buffers = new List<nint>();
        foreach (var slot in slots)
        {
            vkDestroySemaphore(Device.Device, slot.RenderDone, 0);
            vkDestroyFence(Device.Device, slot.Done, 0);
            buffers.Add(slot.Before);
            buffers.Add(slot.After);
        }
        slots = [];
        if (buffers.Count > 0)
        {
            fixed (nint* p = buffers.ToArray())
                vkFreeCommandBuffers(Device.Device, commandPool, (uint)buffers.Count, p);
        }
        if (swapchain != 0)
            vkDestroySwapchainKHR(Device.Device, swapchain, 0);
        swapchain = 0;
    }

    void Record(nint buffer, ulong image, int oldLayout, int newLayout, uint srcAccess, uint dstAccess, uint srcStage, uint dstStage)
    {
        var begin = new VkCommandBufferBeginInfo
        {
            sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO,
            flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT,
        };
        Check(vkBeginCommandBuffer(buffer, &begin), "vkBeginCommandBuffer");
        var barrier = new VkImageMemoryBarrier
        {
            sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
            srcAccessMask = srcAccess,
            dstAccessMask = dstAccess,
            oldLayout = oldLayout,
            newLayout = newLayout,
            srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            image = image,
            aspectMask = VK_IMAGE_ASPECT_COLOR_BIT,
            levelCount = 1,
            layerCount = 1,
        };
        vkCmdPipelineBarrier(buffer, srcStage, dstStage, 0, 0, null, 0, null, 1, &barrier);
        Check(vkEndCommandBuffer(buffer), "vkEndCommandBuffer");
    }

    void Submit(nint buffer, ulong signal, ulong fence)
    {
        var info = new VkSubmitInfo
        {
            sType = VK_STRUCTURE_TYPE_SUBMIT_INFO,
            commandBufferCount = 1,
            pCommandBuffers = &buffer,
            signalSemaphoreCount = signal != 0 ? 1u : 0,
            pSignalSemaphores = &signal,
        };
        Check(vkQueueSubmit(Device.Queue, 1, &info, fence), "vkQueueSubmit");
    }

    void WaitAndReset(ulong fence)
    {
        Check(vkWaitForFences(Device.Device, 1, &fence, 1, ulong.MaxValue), "vkWaitForFences");
        Check(vkResetFences(Device.Device, 1, &fence), "vkResetFences");
    }

    ulong CreateFence(bool signaled)
    {
        var info = new VkFenceCreateInfo { sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO, flags = signaled ? 1u : 0 };
        ulong fence;
        Check(vkCreateFence(Device.Device, &info, 0, &fence), "vkCreateFence");
        return fence;
    }

    ulong CreateSemaphore()
    {
        var info = new VkFenceCreateInfo { sType = VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO };
        ulong semaphore;
        Check(vkCreateSemaphore(Device.Device, &info, 0, &semaphore), "vkCreateSemaphore");
        return semaphore;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        DestroySlots();
        vkDestroyFence(Device.Device, acquireFence, 0);
        vkDestroyCommandPool(Device.Device, commandPool, 0);
        vkDestroySurfaceKHR(Device.Instance, vkSurface, 0);
    }
}
