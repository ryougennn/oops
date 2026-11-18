using System.Drawing;
using oopshell.Skia;
using SkiaSharp;

namespace oopshell.IntegrationTests;

[Collection("sway")]
public class SkiaTests(SwayFixture sway)
{
    static void DrawScene(SKCanvas canvas, float scale)
    {
        canvas.Scale(scale);
        canvas.Clear(new SKColor(0x00, 0x80, 0x80));
        using var gray = new SKPaint { Color = new SKColor(0xC0, 0xC0, 0xC0) };
        using var navy = new SKPaint { Color = new SKColor(0x00, 0x00, 0x80) };
        using var white = new SKPaint { Color = SKColors.White };
        canvas.DrawRect(100, 100, 400, 250, gray);
        canvas.DrawRect(103, 103, 394, 24, navy);
        canvas.DrawRect(100, 100, 400, 2, white);
        canvas.DrawRect(600, 400, 150, 150, navy);
    }

    Png RenderWith(SkiaBackend backend)
    {
        using var app = sway.Connect();
        using var window = new Toplevel(app);
        window.SetFullscreen(true);
        using var skia = new SkiaSurface(window, backend);
        if (backend == SkiaBackend.Gpu)
            Assert.True(skia.IsGpu);
        int frames = 0;
        window.Render += frame =>
        {
            if (skia.BeginFrame(frame) is not { } canvas)
                return;
            DrawScene(canvas, (float)frame.Scale);
            skia.EndFrame();
            frames++;
        };
        app.RunUntil(() => frames >= 1 && window.LogicalSize == new Size(800, 600), what: "skia frame");
        app.RunFor(100);
        return sway.Screenshot();
    }

    [Fact]
    public void Shm_backend_matches_golden()
    {
        sway.RequireSway();
        RenderWith(SkiaBackend.Shm).AssertMatchesGolden("skia-scene");
    }

    [Fact]
    [Trait("Requires", "GPU")]
    public void Gpu_backend_renders_the_same_as_shm()
    {
        sway.RequireSway();
        Png gpu;
        try
        {
            gpu = RenderWith(SkiaBackend.Gpu);
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException && e.Message.Contains("EGL"))
        {
            Assert.Skip("EGL unavailable: " + e.Message);
            return;
        }
        // Compare against the SHM golden, without rewriting it.
        var golden = Png.Decode(File.ReadAllBytes(Path.Combine(SwayFixture.RepoRoot, "tests/goldens/skia-scene.png")));
        Assert.Equal(golden.Rgba.Length, gpu.Rgba.Length);
        int maxDiff = golden.Rgba.Zip(gpu.Rgba, (a, b) => Math.Abs(a - b)).Max();
        Assert.True(maxDiff <= 2, $"GPU and SHM differ by up to {maxDiff} per channel.");
    }
}
