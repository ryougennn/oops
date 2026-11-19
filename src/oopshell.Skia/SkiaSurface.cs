using System.Drawing;
using oopshell.Egl;
using oopshell.Vulkan;
using SkiaSharp;

namespace oopshell.Skia;

/// <summary>Which Skia backend to use.</summary>
public enum SkiaBackend
{
    /// <summary>GPU (OpenGL ES via EGL) when EGL works, otherwise SHM.</summary>
    Auto,
    /// <summary>OpenGL ES via EGL.</summary>
    Gpu,
    Shm,
    /// <summary>Vulkan. Falls back like <see cref="Auto"/> when Vulkan is unavailable.</summary>
    Vulkan,
}

/// <summary>
/// Draw a <see cref="Surface"/> with SkiaSharp. In a <see cref="Surface.Render"/> handler call
/// <see cref="BeginFrame"/>, draw on the canvas (buffer pixels, top-left origin; scale by
/// <see cref="RenderFrame.Scale"/> for logical coordinates), then <see cref="EndFrame"/>.
/// The canvas is clipped to the area that needs repainting given the buffer's age, so redrawing
/// everything every frame is correct and cheap.
/// </summary>
public sealed class SkiaSurface : IDisposable
{
    private const int GL_RGBA8 = 0x8058;

    private readonly Surface _surface;
    // GPU
    private readonly EglWindow? _egl;
    private readonly GRContext? _grContext;
    private readonly DamageHistory _gpuHistory = new();
    private SKSurface? _gpuSurface;
    private Size _gpuSize;
    // Vulkan
    private readonly VulkanWindow? _vulkan;
    private SKSurface?[] _vulkanSurfaces = [];
    // SHM
    private readonly ShmSwapchain? _chain;
    private readonly Dictionary<nint, SKSurface> _shmSurfaces = new();
    private Size _shmSize;
    private ShmBuffer? _shmBuffer;

    private RenderFrame _frame;
    private SKSurface? _current;

    public SkiaSurface(Surface surface, SkiaBackend backend = SkiaBackend.Auto)
    {
        _surface = surface;
        if (backend == SkiaBackend.Vulkan)
        {
            try
            {
                _vulkan = VulkanWindow.Create(surface);
                _grContext = SharedContext(_vulkan.Device);
                _vulkan.ImagesDestroying += DropVulkanSurfaces;
                return;
            }
            catch (Exception e) when (e is NotSupportedException or InvalidOperationException)
            {
                _vulkan?.Dispose();
                _vulkan = null;
                backend = SkiaBackend.Auto;
            }
        }
        if (backend != SkiaBackend.Shm)
        {
            try
            {
                _egl = EglWindow.Create(surface);
                _grContext = SharedContext(_egl.Display);
            }
            catch (Exception e) when (backend == SkiaBackend.Auto && e is NotSupportedException or InvalidOperationException)
            {
                _egl?.Dispose();
                _egl = null;
            }
        }
        if (_egl is null)
            _chain = new ShmSwapchain(surface);
    }

    /// <summary>True when drawing on the GPU (EGL or Vulkan).</summary>
    public bool IsGpu => _egl is not null || _vulkan is not null;

    /// <summary>True when drawing with Vulkan.</summary>
    public bool IsVulkan => _vulkan is not null;

    /// <summary>The GPU context (shared by all GPU surfaces of the app), or null on SHM.</summary>
    public GRContext? GRContext => _grContext;

    /// <summary>
    /// Starts a frame. Returns null when no SHM buffer is free; skip drawing then (the surface is invalidated
    /// again when one is released).
    /// </summary>
    public SKCanvas? BeginFrame(RenderFrame frame)
    {
        if (_current is not null)
            throw new InvalidOperationException("EndFrame was not called for the previous frame.");
        _frame = frame;
        Rectangle repair;
        if (_vulkan is not null)
        {
            int index = _vulkan.Acquire(frame.BufferSize);
            if (_vulkanSurfaces.Length <= index)
                Array.Resize(ref _vulkanSurfaces, index + 1);
            _current = _vulkanSurfaces[index] ??= WrapVulkanImage(_vulkan.GetImage(index));
            // shortcut: Vulkan has no buffer age, so every frame repaints the whole buffer; track per-image
            // damage like DamageHistory does if partial repaints ever matter here.
            repair = new Rectangle(Point.Empty, frame.BufferSize);
        }
        else if (_egl is not null)
        {
            _egl.Resize(frame.BufferSize);
            _egl.MakeCurrent();
            // The GRContext is shared by every GPU surface of the app; after switching EGL surfaces its cached
            // GL state (bound framebuffer, viewport, scissor) is stale.
            _grContext!.ResetContext();
            if (_gpuSurface is null || _gpuSize != frame.BufferSize)
            {
                _gpuSurface?.Dispose();
                _gpuSize = frame.BufferSize;
                _gpuHistory.Clear();
                using var target = new GRBackendRenderTarget(_gpuSize.Width, _gpuSize.Height, _egl.Display.Options.Samples,
                    _egl.Display.Options.StencilBits, new GRGlFramebufferInfo(0, GL_RGBA8));
                _gpuSurface = SKSurface.Create(_grContext, target, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888)
                    ?? throw new InvalidOperationException("Skia could not wrap the EGL framebuffer.");
            }
            _current = _gpuSurface;
            repair = _gpuHistory.RepairArea(_egl.BufferAge, frame.Damage, frame.BufferSize);
        }
        else
        {
            _shmBuffer = _chain!.Acquire(frame);
            if (_shmBuffer is null)
                return null;
            _current = ShmSurface(_shmBuffer);
            repair = _shmBuffer.Damage;
        }

        var canvas = _current.Canvas;
        canvas.Save();
        canvas.ResetMatrix();
        canvas.ClipRect(SKRect.Create(repair.X, repair.Y, repair.Width, repair.Height));
        return canvas;
    }

    /// <summary>Presents what was drawn since <see cref="BeginFrame"/>.</summary>
    public void EndFrame()
    {
        if (_current is null)
            return;
        _current.Canvas.Restore();
        if (_vulkan is not null)
        {
            var flash = _surface.App.ReloadFlashRect(_frame.BufferSize, _frame.Scale);
            if (!flash.IsEmpty)
            {
                using var magenta = new SKPaint { Color = SKColors.Magenta };
                _current.Canvas.DrawRect(flash.X, flash.Y, flash.Width, flash.Height, magenta);
            }
            _grContext!.Flush(submit: true, synchronous: false);
            _vulkan.Present();
            _current = null;
            return;
        }
        _current.Flush();
        if (_egl is not null)
        {
            _egl.SwapBuffers(_frame.Damage);
            _gpuHistory.Add(_frame.Damage);
        }
        else
        {
            _shmBuffer!.Present(_frame.Damage);
            _shmBuffer = null;
        }
        _current = null;
    }

    private SKSurface ShmSurface(ShmBuffer buffer)
    {
        if (_shmSize != buffer.Size)
        {
            foreach (var old in _shmSurfaces.Values)
                old.Dispose();
            _shmSurfaces.Clear();
            _shmSize = buffer.Size;
        }
        if (!_shmSurfaces.TryGetValue(buffer.Pointer, out var skSurface))
        {
            // wl_shm ARGB8888 is BGRA in memory on little-endian machines.
            var info = new SKImageInfo(buffer.Size.Width, buffer.Size.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            skSurface = SKSurface.Create(info, buffer.Pointer, buffer.Stride)
                ?? throw new InvalidOperationException("Skia could not wrap the SHM buffer.");
            _shmSurfaces[buffer.Pointer] = skSurface;
        }
        return skSurface;
    }

    private SKSurface WrapVulkanImage(ulong image)
    {
        var info = new GRVkImageInfo
        {
            Image = image,
            ImageTiling = Native.Vulkan.VK_IMAGE_TILING_OPTIMAL,
            ImageLayout = Native.Vulkan.VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL,
            Format = (uint)_vulkan!.Format,
            ImageUsageFlags = _vulkan.ImageUsage,
            SampleCount = 1,
            LevelCount = 1,
            CurrentQueueFamily = _vulkan.Device.QueueFamily,
        };
        var colorType = _vulkan.Format == Native.Vulkan.VK_FORMAT_B8G8R8A8_UNORM ? SKColorType.Bgra8888 : SKColorType.Rgba8888;
        using var target = new GRBackendRenderTarget(_vulkan.Size.Width, _vulkan.Size.Height, info);
        return SKSurface.Create(_grContext, target, GRSurfaceOrigin.TopLeft, colorType)
            ?? throw new InvalidOperationException("Skia could not wrap the Vulkan swapchain image.");
    }

    private void DropVulkanSurfaces()
    {
        foreach (var s in _vulkanSurfaces)
            s?.Dispose();
        _vulkanSurfaces = [];
        // Let Skia finish and release what referenced the images before they are destroyed.
        _grContext!.Flush(submit: true, synchronous: true);
    }

    private static readonly Dictionary<VulkanDevice, GRContext> s_vulkanContexts = new();

    private static GRContext SharedContext(VulkanDevice device)
    {
        if (s_vulkanContexts.TryGetValue(device, out var context))
            return context;
        GRVkGetProcedureAddressDelegate getProc = VulkanDevice.GetProcAddress;
        // Kept alive with the context: Skia may still call back into getProc after creation.
        var extensions = GRVkExtensions.Create(getProc, device.Instance, device.PhysicalDevice,
            VulkanDevice.InstanceExtensions, VulkanDevice.DeviceExtensions);
        var backend = new GRVkBackendContext
        {
            VkInstance = device.Instance,
            VkPhysicalDevice = device.PhysicalDevice,
            VkDevice = device.Device,
            VkQueue = device.Queue,
            GraphicsQueueIndex = device.QueueFamily,
            MaxAPIVersion = device.ApiVersion,
            Extensions = extensions,
            VkPhysicalDeviceFeatures = device.EnabledFeatures,
            GetProcedureAddress = getProc,
        };
        context = GRContext.CreateVulkan(backend) ?? throw new InvalidOperationException("Skia could not create a Vulkan context.");
        s_vulkanContexts[device] = context;
        device.Disposing += () =>
        {
            s_vulkanContexts.Remove(device);
            context.AbandonContext(releaseResources: false);
            context.Dispose();
            backend.Dispose();
            extensions.Dispose();
        };
        return context;
    }

    private static readonly Dictionary<EglDisplay, GRContext> s_contexts = new();

    private static GRContext SharedContext(EglDisplay display)
    {
        if (s_contexts.TryGetValue(display, out var context))
            return context;
        display.MakeCurrentWithoutSurface(); // Skia queries GL while building its function table
        using var glInterface = (display.Options.Api == GlApi.GLES
            ? GRGlInterface.CreateGles(EglDisplay.GetProcAddress)
            : GRGlInterface.CreateOpenGl(EglDisplay.GetProcAddress))
            ?? throw new InvalidOperationException("Skia could not load GL functions.");
        context = GRContext.CreateGl(glInterface) ?? throw new InvalidOperationException("Skia could not create a GL context.");
        s_contexts[display] = context;
        display.Disposing += () =>
        {
            // The GL context is about to be destroyed; Skia must not touch it afterwards.
            s_contexts.Remove(display);
            context.AbandonContext(releaseResources: false);
            context.Dispose();
        };
        return context;
    }

    public void Dispose()
    {
        _shmBuffer?.Dispose();
        foreach (var s in _shmSurfaces.Values)
            s.Dispose();
        _shmSurfaces.Clear();
        _chain?.Dispose();
        if (_vulkan is not null)
        {
            _vulkan.Dispose(); // raises ImagesDestroying, which drops the SKSurfaces first
        }
        if (_egl is not null)
        {
            _egl.MakeCurrent();
            _gpuSurface?.Dispose();
            _grContext?.Flush();
            _egl.Dispose();
        }
    }
}
