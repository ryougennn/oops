using System.Drawing;

namespace oopshell.IntegrationTests;

[Collection("sway")]
public class LayerSurfaceTests(SwayFixture sway)
{
    static LayerSurface CreateBar(OopsApp app, Output? output, int height)
    {
        return new LayerSurface(app, output, Layer.Top, "test-bar")
        {
            Anchor = Anchor.Bottom | Anchor.Left | Anchor.Right,
            Size = new Size(0, height),
            ExclusiveZone = height,
        };
    }

    [Fact]
    public void Exclusive_zone_shrinks_toplevels()
    {
        using var app = sway.Connect();
        using var bar = CreateBar(app, app.Outputs[0], 30);
        var barFrames = Draw.SolidColor(bar, 0xFF102030);
        app.RunUntil(() => barFrames() >= 1, what: "bar frame");
        Assert.Equal(new Size(800, 30), bar.LogicalSize);

        using var window = new Toplevel(app);
        var frames = Draw.SolidColor(window, 0xFFFFFFFF);
        app.RunUntil(() => frames() >= 1 && window.LogicalSize == new Size(800, 570), what: "window above the bar");

        var shot = sway.Screenshot();
        Assert.Equal((0x10, 0x20, 0x30), shot[400, 590]);
        Assert.Equal((255, 255, 255), shot[400, 560]);
    }

    [Fact]
    public void ForEachOutput_follows_hotplug()
    {
        using var app = sway.Connect();
        var bars = new Dictionary<string, LayerSurface>();
        var disposed = new List<string>();
        using var subscription = app.ForEachOutput(output =>
        {
            var bar = CreateBar(app, output, 20);
            Draw.SolidColor(bar, 0xFF000000);
            bars[output.Name] = bar;
            return new Disposer(() =>
            {
                disposed.Add(output.Name);
                bar.Dispose();
            });
        });
        Assert.Equal(["HEADLESS-1"], bars.Keys);

        sway.Swaymsg("create_output");
        app.RunUntil(() => bars.Count == 2, what: "second output");
        string added = bars.Keys.Single(k => k != "HEADLESS-1");
        app.RunUntil(() => bars[added].IsConfigured, what: "bar on new output");
        Assert.Equal(app.Outputs.Single(o => o.Name == added).LogicalSize.Width, bars[added].LogicalSize.Width);

        sway.Swaymsg("output", added, "unplug");
        app.RunUntil(() => disposed.Contains(added), what: "unplug");
        Assert.Single(app.Outputs);
        Assert.DoesNotContain("HEADLESS-1", disposed);
    }

    [Fact]
    public void Empty_input_region_is_click_through()
    {
        using var app = sway.Connect();
        using var input = new VirtualInput(app);
        using var window = new Toplevel(app);
        var windowFrames = Draw.SolidColor(window, 0xFF000000);
        app.RunUntil(() => windowFrames() >= 1 && window.LogicalSize.Width == 800, what: "window");

        using var overlay = new LayerSurface(app, app.Outputs[0], Layer.Overlay, "test-overlay")
        {
            Anchor = Anchor.All,
            InputRegion = Region.Empty,
        };
        var overlayFrames = Draw.SolidColor(overlay, 0x40FF0000);
        bool overlayEntered = false;
        overlay.PointerEnter += _ => overlayEntered = true;
        app.RunUntil(() => overlayFrames() >= 1, what: "overlay");

        var seat = app.DefaultSeat!;
        input.MoveTo(400, 300);
        input.MoveTo(401, 301);
        app.RunUntil(() => seat.PointerFocus == window && seat.PointerX == 401, what: "pointer reaching the window through the overlay");
        Assert.False(overlayEntered);
    }

    [Fact]
    public void Fractional_scale_sets_buffer_size()
    {
        using var app = sway.Connect();
        using var bar = CreateBar(app, app.Outputs[0], 30);
        var frames = Draw.SolidColor(bar, 0xFF000000);
        var buffers = new List<Size>();
        bar.Render += frame => buffers.Add(frame.BufferSize);
        app.RunUntil(() => frames() >= 1, what: "bar");

        try
        {
            sway.Swaymsg("output", "HEADLESS-1", "scale", "1.5");
            app.RunUntil(() => bar.Scale == 1.5 && frames() >= 2 && bar.LogicalSize.Width == 533, what: "scale 1.5");
            Assert.Equal(new Size(800, 45), bar.BufferSize);
            Assert.Equal(new Size(800, 45), buffers[^1]);

            sway.Swaymsg("output", "HEADLESS-1", "scale", "2");
            app.RunUntil(() => bar.Scale == 2 && bar.LogicalSize.Width == 400, what: "scale 2");
            app.RunUntil(() => buffers[^1] == new Size(800, 60), what: "2x buffer");
        }
        finally
        {
            sway.Swaymsg("output", "HEADLESS-1", "scale", "1");
            app.RunUntil(() => app.Outputs[0].LogicalSize.Width == 800 && bar.Scale == 1, what: "scale reset");
        }
    }

    sealed class Disposer(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
