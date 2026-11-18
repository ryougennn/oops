# Rendering

oopshell does not draw. In `surface.Render += frame => …` you present **exactly once** using one of:

## A. Shared memory (CPU)

```csharp
var chain = new ShmSwapchain(surface);            // ARGB8888, up to 3 buffers (minimum 2)
surface.Render += frame =>
{
    using var buffer = chain.Acquire(frame);       // null: every buffer is still with the compositor
    if (buffer is null) return;                    // skip; you are invalidated again on release
    Span<uint> pixels = buffer.Pixels;             // premultiplied 0xAARRGGBB, Stride bytes per row
    // redraw at least buffer.Damage (this frame's damage + whatever changed since the buffer's Age)
    buffer.Present(frame.Damage);
};
```

`buffer.Pointer` gives the raw address for libraries that want one.

## B. EGL (GPU), package `oopshell.Egl`

```csharp
using var egl = EglWindow.Create(surface);         // GLES 3 by default; one shared context per app
surface.Render += frame =>
{
    egl.Resize(frame.BufferSize);
    egl.MakeCurrent();
    int age = egl.BufferAge;                       // EGL_EXT_buffer_age, 0 = undefined
    // GL calls; load functions with EglDisplay.GetProcAddress
    egl.SwapBuffers(frame.Damage);                 // swap-with-damage when available
};
```

Swap interval is 0: pacing comes from oopshell's frame callbacks. The EGL surface is created on the
first `MakeCurrent`, at the configured size.

## C. Raw handles (Vulkan, wgpu, …)

`surface.NativeDisplay` (`wl_display*`) and `surface.NativeSurface` (`wl_surface*`) are what
`vkCreateWaylandSurfaceKHR` needs:

```csharp
var createInfo = new VkWaylandSurfaceCreateInfoKHR
{
    sType = VkStructureType.WaylandSurfaceCreateInfoKHR,
    display = surface.NativeDisplay,
    surface = surface.NativeSurface,
};
// vkCreateWaylandSurfaceKHR(instance, &createInfo, null, &vkSurface); then your own swapchain.
surface.Render += frame =>
{
    surface.PrepareCommit();                       // frame callback + pending scale/viewport/regions
    // vkQueuePresentKHR(...) — present mode FIFO is fine, or MAILBOX/IMMEDIATE with oopshell pacing
};
```

Call `PrepareCommit()` right before your own commit/present so oopshell keeps pacing frames.

## Frame contract

* `RenderFrame` carries `BufferSize`, `LogicalSize`, `Scale`, `Damage` (buffer pixels) and `Time`.
* The first frame after a configure or scale change is immediate and fully damaged; after that at most
  one frame per frame callback.
* Scale: fractional (wp_fractional_scale + viewporter) when available, else integer
  (`preferred_buffer_scale` or the entered outputs). `BufferSize = round(LogicalSize × Scale)`.
* `surface.Presented` reports presentation-time feedback (only requested while someone listens).
