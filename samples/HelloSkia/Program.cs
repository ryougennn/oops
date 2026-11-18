// A Skia-drawn window: GPU through EGL when available (--vulkan for Vulkan, --shm to force CPU), crisp at any scale
// because everything is drawn in logical units scaled by the frame's scale. Edit Scene.Draw while
// running under `dotnet watch` to see hot reload.
using oopshell;
using oopshell.Skia;
using SkiaSharp;

using var app = OopsApp.Connect();
using var window = new Toplevel(app) { Title = "HelloSkia", AppId = "oopshell.HelloSkia" };
using var skia = new SkiaSurface(window, args.Contains("--shm") ? SkiaBackend.Shm : args.Contains("--vulkan") ? SkiaBackend.Vulkan : SkiaBackend.Auto);
Console.WriteLine($"Rendering on the {(skia.IsVulkan ? $"GPU (Vulkan, {oopshell.Vulkan.VulkanDevice.Get(app).DeviceName})" : skia.IsGpu ? "GPU (EGL)" : "CPU (SHM)")}");

window.CloseRequested += () => app.Quit();
window.Render += frame =>
{
    if (skia.BeginFrame(frame) is not { } canvas)
        return;
    canvas.Scale((float)frame.Scale);
    Scene.Draw(canvas, frame.LogicalSize.Width, frame.LogicalSize.Height, frame.Scale);
    skia.EndFrame();
};

return app.Run();

static class Scene
{
    static readonly SKPaint Text = new() { Color = SKColors.White, IsAntialias = true };
    // Skia's fontconfig lookup does not resolve generic names like "sans-serif" everywhere; fall back to
    // whatever font covers Latin text.
    static readonly SKFont Font = new(SKTypeface.FromFamilyName("sans-serif") is { FamilyName.Length: > 0 } sans
        ? sans : SKFontManager.Default.MatchCharacter('A'), 28);
    static readonly SKPaint Accent = new() { Color = new SKColor(0xFF, 0x8C, 0x42), IsAntialias = true };

    public static void Draw(SKCanvas canvas, int width, int height, double scale)
    {
        canvas.Clear(new SKColor(0x1E, 0x24, 0x30));
        canvas.DrawCircle(width / 2f, height / 2f - 30, 60, Accent);
        canvas.DrawText("Hello, Skia", width / 2f, height / 2f + 70, SKTextAlign.Center, Font, Text);
        canvas.DrawText($"scale {scale:0.##}", 12, height - 12, SKTextAlign.Left, Font, Text);
    }
}
