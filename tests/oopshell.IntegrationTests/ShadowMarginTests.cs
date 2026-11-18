using System.Drawing;
using oopshell.Protocol;

namespace oopshell.IntegrationTests;

[Collection("sway")]
public class ShadowMarginTests(SwayFixture sway)
{
    const uint Margin = 0xFF00C000, Content = 0xFFC00000;

    /// <summary>Paints the margins green and the window geometry red.</summary>
    static Func<int> PaintMargins(Surface surface, Func<Margins> margins)
    {
        var chain = new ShmSwapchain(surface);
        int frames = 0;
        surface.Render += frame =>
        {
            if (chain.Acquire(frame) is not { } buffer)
                return;
            var m = margins();
            var size = frame.BufferSize;
            var pixels = buffer.Pixels;
            for (int y = 0; y < size.Height; y++)
                for (int x = 0; x < size.Width; x++)
                {
                    bool inside = x >= m.Left && x < size.Width - m.Right && y >= m.Top && y < size.Height - m.Bottom;
                    pixels[y * size.Width + x] = inside ? Content : Margin;
                }
            buffer.Present();
            frames++;
        };
        return () => frames;
    }

    [Fact]
    public void Toplevel_is_tiled_by_its_geometry_and_draws_its_margins()
    {
        using var app = sway.Connect();
        using var window = new Toplevel(app) { ShadowMargins = new Margins(10, 20, 30, 40) };
        var frames = PaintMargins(window, () => window.ShadowMargins);
        app.RunUntil(() => frames() >= 1 && window.GeometrySize.Width == 800, what: "configured window");

        Assert.Equal(new Size(800, 600), window.GeometrySize);
        Assert.Equal(new Size(860, 640), window.LogicalSize);
        var shot = sway.Screenshot();
        Assert.Equal((0xC0, 0x00, 0x00), shot[0, 0]);     // the geometry, not the left/top margin, is at the tile's corner
        Assert.Equal((0xC0, 0x00, 0x00), shot[799, 599]);

        // Margins can change later; back to none means the whole surface is the window again.
        window.ShadowMargins = default;
        int before = frames();
        app.RunUntil(() => frames() > before && window.LogicalSize == new Size(800, 600), what: "margins removed");
    }

    [Fact]
    public void Popup_is_positioned_by_its_geometry_with_the_shadow_around_it()
    {
        using var app = sway.Connect();
        using var window = new Toplevel(app);
        var windowFrames = Draw.SolidColor(window, 0xFF202020);
        app.RunUntil(() => windowFrames() >= 1, what: "window");

        using var popup = new Popup(window, new PopupOptions
        {
            Size = new Size(100, 60),
            AnchorRect = new Rectangle(200, 150, 1, 1),
            Anchor = XdgPositionerAnchor.TopLeft,
            Gravity = XdgPositionerGravity.BottomRight,
        }) { ShadowMargins = Margins.All(6) };
        var frames = PaintMargins(popup, () => popup.ShadowMargins);
        app.RunUntil(() => frames() >= 1, what: "popup");

        Assert.Equal(new Rectangle(200, 150, 100, 60), popup.Geometry);
        Assert.Equal(new Size(112, 72), popup.LogicalSize);
        var shot = sway.Screenshot();
        Assert.Equal((0xC0, 0x00, 0x00), shot[200, 150]);
        Assert.Equal((0xC0, 0x00, 0x00), shot[299, 209]);
        Assert.Equal((0x00, 0xC0, 0x00), shot[196, 150]);  // the shadow margin sticks out of the geometry
        Assert.Equal((0x00, 0xC0, 0x00), shot[250, 213]);
    }
}
