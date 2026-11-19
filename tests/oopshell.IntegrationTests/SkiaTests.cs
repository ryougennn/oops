using System.Drawing;
using oopshell.Skia;
using SkiaSharp;

namespace oopshell.IntegrationTests;

[Collection("sway")]
public class SkiaTests(SwayFixture sway)
{
    private static void DrawScene(SKCanvas canvas, float scale)
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

    private Png RenderWith(SkiaBackend backend)
    {
        using var app = sway.Connect();
        using var window = new Toplevel(app);
        window.SetFullscreen(true);
        using var skia = new SkiaSurface(window, backend);
        if (backend == SkiaBackend.Gpu)
            Assert.True(skia.IsGpu);
        if (backend == SkiaBackend.Vulkan)
            Assert.SkipUnless(skia.IsVulkan, "Vulkan unavailable");
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

    /// <summary>
    /// Regression: an EGL window created before the first configure presented its first frame from a 1×1
    /// buffer (stretched to a black rectangle by the viewport). EGL surfaces are now created lazily.
    /// </summary>
    [Fact]
    [Trait("Requires", "GPU")]
    public void Gpu_popup_first_frame_has_the_configured_size()
    {
        using var app = sway.Connect();
        using var window = new Toplevel(app);
        var windowFrames = Draw.SolidColor(window, 0xFF000000);
        app.RunUntil(() => windowFrames() >= 1 && window.LogicalSize.Width == 800, what: "window");

        using var popup = new Popup(window, new PopupOptions { Size = new System.Drawing.Size(150, 100), AnchorRect = new System.Drawing.Rectangle(100, 100, 1, 1) });
        using var skia = new SkiaSurface(popup, SkiaBackend.Auto);
        Assert.SkipUnless(skia.IsGpu, "EGL unavailable");
        int frames = 0;
        popup.Render += frame =>
        {
            if (skia.BeginFrame(frame) is not { } canvas)
                return;
            canvas.Clear(new SKColor(0x10, 0xC0, 0x20));
            skia.EndFrame();
            frames++;
        };
        app.RunUntil(() => frames >= 1, what: "popup frame");
        app.RunFor(100);
        var shot = sway.Screenshot();
        Assert.Equal((0x10, 0xC0, 0x20), shot[101, 101]);
        Assert.Equal((0x10, 0xC0, 0x20), shot[248, 198]);
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

    [Fact]
    [Trait("Requires", "GPU")]
    public void Vulkan_backend_renders_the_same_as_shm()
    {
        sway.RequireSway();
        var vulkan = RenderWith(SkiaBackend.Vulkan);
        var golden = Png.Decode(File.ReadAllBytes(Path.Combine(SwayFixture.RepoRoot, "tests/goldens/skia-scene.png")));
        Assert.Equal(golden.Rgba.Length, vulkan.Rgba.Length);
        int maxDiff = golden.Rgba.Zip(vulkan.Rgba, (a, b) => Math.Abs(a - b)).Max();
        Assert.True(maxDiff <= 2, $"Vulkan and SHM differ by up to {maxDiff} per channel.");
    }

    /// <summary>
    /// Regression: blend modes that read the destination crashed Skia inside its Vulkan render pass setup while
    /// the swapchain images had INPUT_ATTACHMENT usage.
    /// </summary>
    [Fact]
    [Trait("Requires", "GPU")]
    public void Vulkan_blend_modes_that_read_the_destination_work()
    {
        using var app = sway.Connect();
        using var window = new Toplevel(app);
        window.SetFullscreen(true);
        using var skia = new SkiaSurface(window, SkiaBackend.Vulkan);
        Assert.SkipUnless(skia.IsVulkan, "Vulkan unavailable");
        int frames = 0;
        window.Render += frame =>
        {
            if (skia.BeginFrame(frame) is not { } canvas)
                return;
            canvas.Clear(new SKColor(0xFF, 0xFF, 0x00));
            using var multiply = new SKPaint { Color = new SKColor(0x00, 0xFF, 0xFF), BlendMode = SKBlendMode.Multiply };
            canvas.DrawRect(100, 100, 200, 200, multiply);
            skia.EndFrame();
            frames++;
        };
        app.RunUntil(() => frames >= 1 && window.LogicalSize == new Size(800, 600), what: "frame");
        app.RunFor(100);
        var shot = sway.Screenshot();
        Assert.Equal((0x00, 0xFF, 0x00), shot[200, 200]);
        Assert.Equal((0xFF, 0xFF, 0x00), shot[50, 50]);
    }

    /// <summary>A resize recreates the swapchain; the next frame has the new size and the old images are released cleanly.</summary>
    [Fact]
    [Trait("Requires", "GPU")]
    public void Vulkan_swapchain_follows_resizes()
    {
        using var app = sway.Connect();
        using var window = new Toplevel(app);
        using var skia = new SkiaSurface(window, SkiaBackend.Vulkan);
        Assert.SkipUnless(skia.IsVulkan, "Vulkan unavailable");
        var sizes = new List<Size>();
        window.Render += frame =>
        {
            if (skia.BeginFrame(frame) is not { } canvas)
                return;
            canvas.Clear(new SKColor(0xC0, 0x20, 0x40));
            skia.EndFrame();
            sizes.Add(frame.BufferSize);
        };
        app.RunUntil(() => sizes.Count >= 1 && sizes[^1] == new Size(800, 600), what: "first frame");

        // A second tiled window halves the first one.
        using var other = new Toplevel(app);
        var otherFrames = Draw.SolidColor(other, 0xFF000000);
        app.RunUntil(() => otherFrames() >= 1 && sizes[^1] == new Size(400, 600), what: "frame at half width");
        app.RunFor(100);
        Assert.Equal((0xC0, 0x20, 0x40), sway.Screenshot()[200, 300]);
    }
}
