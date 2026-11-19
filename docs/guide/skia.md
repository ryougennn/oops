# Drawing with Skia

Package `oopshell.Skia` (SkiaSharp 4.157) wraps an `EglWindow` (GPU, OpenGL ES), a `VulkanWindow`
(GPU, Vulkan) or a `ShmSwapchain` (CPU). `Auto` picks EGL when it works, else SHM; Vulkan is opt-in
(`SkiaBackend.Vulkan`, falling back like `Auto` when Vulkan is unavailable).

```csharp
using var skia = new SkiaSurface(window);          // SkiaBackend.Auto | Gpu | Vulkan | Shm
window.Render += frame =>
{
    if (skia.BeginFrame(frame) is not { } canvas) return;
    canvas.Scale((float)frame.Scale);               // draw in logical pixels
    canvas.Clear(SKColors.White);
    canvas.DrawText("hi", 10, 20, font, paint);
    skia.EndFrame();
};
```

The canvas is clipped to what actually needs repainting (frame damage plus buffer-age history), so
redrawing the whole scene every frame is both correct and cheap.

All GPU surfaces of an app share one GL context (or one Vulkan device) and one `GRContext` (`skia.GRContext`).

Vulkan vs GL: both draw the same pixels (tested against the same golden). Vulkan always repaints the
whole buffer (no buffer age), GL repaints only the damaged area. Measured on an RX 7900 XT (RADV / radeonsi):
steady frames cost the same (~0.5 ms for a busy scene); the first frame is 24 ms on Vulkan vs 30 ms on GL;
memory is the same (~42 MB PSS for a full desktop shell).

Shader caching: SkiaSharp does not expose Skia's persistent program cache (`GRContextOptions` has no
`PersistentCache`, `libSkiaSharp.so` exports no hook), so Skia regenerates its shaders once per process.
The driver's own disk cache (Mesa: `~/.cache/mesa_shader_cache`, on by default; NVIDIA has its own) keeps
the expensive part, compiling to GPU code, across runs: disabling it (`MESA_SHADER_CACHE_DISABLE=true`)
makes the first frame ~9 ms slower on GL and ~8 ms on Vulkan in the measurement above. Later frames
are unaffected because every shader is cached in memory after its first use.

Fonts: Skia's fontconfig lookup does not resolve generic names like `sans-serif` on every system; ship a
font (`SKTypeface.FromStream`) or fall back to `SKFontManager.Default.MatchCharacter('A')`.

For crisp pixel art set `SKFont.Edging = SKFontEdging.Alias` and draw without antialiasing.
