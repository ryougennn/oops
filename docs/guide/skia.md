# Drawing with Skia

Package `oopshell.Skia` (SkiaSharp 3.119) wraps either an `EglWindow` (GPU) or a `ShmSwapchain`
(CPU), picking the GPU when EGL works.

```csharp
using var skia = new SkiaSurface(window);          // SkiaBackend.Auto | Gpu | Shm
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

All GPU surfaces of an app share one GL context and one `GRContext` (`skia.GRContext`).

Fonts: Skia's fontconfig lookup does not resolve generic names like `sans-serif` on every system; ship a
font (`SKTypeface.FromStream`) or fall back to `SKFontManager.Default.MatchCharacter('A')`.

For crisp pixel art set `SKFont.Edging = SKFontEdging.Alias` and draw without antialiasing.
