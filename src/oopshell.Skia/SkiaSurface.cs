using System.Drawing;
using oopshell.Egl;
using SkiaSharp;

namespace oopshell.Skia;

/// <summary>Which Skia backend to use.</summary>
public enum SkiaBackend
{
    /// <summary>GPU when EGL works, otherwise SHM.</summary>
    Auto,
    Gpu,
    Shm,
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
    const int GL_RGBA8 = 0x8058;

    readonly Surface surface;
    // GPU
    readonly EglWindow? egl;
    readonly GRContext? grContext;
    readonly DamageHistory gpuHistory = new();
    SKSurface? gpuSurface;
    Size gpuSize;
    // SHM
    readonly ShmSwapchain? chain;
    readonly Dictionary<nint, SKSurface> shmSurfaces = new();
    Size shmSize;
    ShmBuffer? shmBuffer;

    RenderFrame frame;
    SKSurface? current;

    public SkiaSurface(Surface surface, SkiaBackend backend = SkiaBackend.Auto)
    {
        this.surface = surface;
        if (backend != SkiaBackend.Shm)
        {
            try
            {
                egl = EglWindow.Create(surface);
                grContext = SharedContext(egl.Display);
            }
            catch (Exception e) when (backend == SkiaBackend.Auto && e is NotSupportedException or InvalidOperationException)
            {
                egl?.Dispose();
                egl = null;
            }
        }
        if (egl is null)
            chain = new ShmSwapchain(surface);
    }

    /// <summary>True when drawing on the GPU.</summary>
    public bool IsGpu => egl is not null;

    /// <summary>The GPU context (shared by all GPU surfaces of the app), or null on SHM.</summary>
    public GRContext? GRContext => grContext;

    /// <summary>
    /// Starts a frame. Returns null when no SHM buffer is free; skip drawing then (the surface is invalidated
    /// again when one is released).
    /// </summary>
    public SKCanvas? BeginFrame(RenderFrame frame)
    {
        if (current is not null)
            throw new InvalidOperationException("EndFrame was not called for the previous frame.");
        this.frame = frame;
        Rectangle repair;
        if (egl is not null)
        {
            egl.Resize(frame.BufferSize);
            egl.MakeCurrent();
            // The GRContext is shared by every GPU surface of the app; after switching EGL surfaces its cached
            // GL state (bound framebuffer, viewport, scissor) is stale.
            grContext!.ResetContext();
            if (gpuSurface is null || gpuSize != frame.BufferSize)
            {
                gpuSurface?.Dispose();
                gpuSize = frame.BufferSize;
                gpuHistory.Clear();
                using var target = new GRBackendRenderTarget(gpuSize.Width, gpuSize.Height, egl.Display.Options.Samples,
                    egl.Display.Options.StencilBits, new GRGlFramebufferInfo(0, GL_RGBA8));
                gpuSurface = SKSurface.Create(grContext, target, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888)
                    ?? throw new InvalidOperationException("Skia could not wrap the EGL framebuffer.");
            }
            current = gpuSurface;
            repair = gpuHistory.RepairArea(egl.BufferAge, frame.Damage, frame.BufferSize);
        }
        else
        {
            shmBuffer = chain!.Acquire(frame);
            if (shmBuffer is null)
                return null;
            current = ShmSurface(shmBuffer);
            repair = shmBuffer.Damage;
        }

        var canvas = current.Canvas;
        canvas.Save();
        canvas.ResetMatrix();
        canvas.ClipRect(SKRect.Create(repair.X, repair.Y, repair.Width, repair.Height));
        return canvas;
    }

    /// <summary>Presents what was drawn since <see cref="BeginFrame"/>.</summary>
    public void EndFrame()
    {
        if (current is null)
            return;
        current.Canvas.Restore();
        current.Flush();
        if (egl is not null)
        {
            egl.SwapBuffers(frame.Damage);
            gpuHistory.Add(frame.Damage);
        }
        else
        {
            shmBuffer!.Present(frame.Damage);
            shmBuffer = null;
        }
        current = null;
    }

    SKSurface ShmSurface(ShmBuffer buffer)
    {
        if (shmSize != buffer.Size)
        {
            foreach (var old in shmSurfaces.Values)
                old.Dispose();
            shmSurfaces.Clear();
            shmSize = buffer.Size;
        }
        if (!shmSurfaces.TryGetValue(buffer.Pointer, out var skSurface))
        {
            // wl_shm ARGB8888 is BGRA in memory on little-endian machines.
            var info = new SKImageInfo(buffer.Size.Width, buffer.Size.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            skSurface = SKSurface.Create(info, buffer.Pointer, buffer.Stride)
                ?? throw new InvalidOperationException("Skia could not wrap the SHM buffer.");
            shmSurfaces[buffer.Pointer] = skSurface;
        }
        return skSurface;
    }

    static readonly Dictionary<EglDisplay, GRContext> Contexts = new();

    static GRContext SharedContext(EglDisplay display)
    {
        if (Contexts.TryGetValue(display, out var context))
            return context;
        display.MakeCurrentWithoutSurface(); // Skia queries GL while building its function table
        using var glInterface = (display.Options.Api == GlApi.GLES
            ? GRGlInterface.CreateGles(EglDisplay.GetProcAddress)
            : GRGlInterface.CreateOpenGl(EglDisplay.GetProcAddress))
            ?? throw new InvalidOperationException("Skia could not load GL functions.");
        context = GRContext.CreateGl(glInterface) ?? throw new InvalidOperationException("Skia could not create a GL context.");
        Contexts[display] = context;
        display.Disposing += () =>
        {
            // The GL context is about to be destroyed; Skia must not touch it afterwards.
            Contexts.Remove(display);
            context.AbandonContext(releaseResources: false);
            context.Dispose();
        };
        return context;
    }

    public void Dispose()
    {
        shmBuffer?.Dispose();
        foreach (var s in shmSurfaces.Values)
            s.Dispose();
        shmSurfaces.Clear();
        chain?.Dispose();
        if (egl is not null)
        {
            egl.MakeCurrent();
            gpuSurface?.Dispose();
            grContext?.Flush();
            egl.Dispose();
        }
    }
}
