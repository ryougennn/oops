using System.Drawing;
using System.Text.Json;

namespace oopshell.IntegrationTests;

[Collection("sway")]
public class ToplevelTests(SwayFixture sway)
{
    [Fact]
    public void Registry_lists_globals_outputs_and_seats()
    {
        using var app = sway.Connect();
        Assert.True(app.Has<oopshell.Protocol.XdgWmBase>());
        Assert.Contains(app.Globals, g => g.Interface == "wl_compositor" && g.Version >= 4);
        var output = Assert.Single(app.Outputs);
        Assert.Equal("HEADLESS-1", output.Name);
        Assert.Equal(new Size(800, 600), output.PixelSize);
        Assert.Equal(new Size(800, 600), output.LogicalSize);
        Assert.Single(app.Seats);
    }

    [Fact]
    public void Toplevel_is_configured_rendered_and_visible()
    {
        using var app = sway.Connect();
        using var window = new Toplevel(app) { Title = "configure-test" };
        var frames = Draw.SolidColor(window, 0xFF3366CC);

        app.RunUntil(() => frames() >= 1 && window.LogicalSize == new Size(800, 600), what: "tiled frame");
        app.RunFor(100);

        Assert.True(window.States.HasFlag(ToplevelStates.Activated));
        Assert.Equal((0x33, 0x66, 0xCC), sway.Screenshot()[400, 300]);
    }

    [Fact]
    public void Toplevel_without_invalidation_renders_once()
    {
        using var app = sway.Connect();
        using var window = new Toplevel(app);
        var frames = Draw.SolidColor(window, 0xFF000000);

        app.RunUntil(() => frames() >= 1 && window.LogicalSize == new Size(800, 600), what: "tiled frame");
        app.RunFor(100);
        int settled = frames();
        app.RunFor(300);

        Assert.Equal(settled, frames());
    }

    [Fact]
    public void Toplevel_is_resized_when_a_second_window_tiles_next_to_it()
    {
        using var app = sway.Connect();
        using var first = new Toplevel(app) { AppId = "oopshell.test.first" };
        var firstFrames = Draw.SolidColor(first, 0xFFFF0000);
        app.RunUntil(() => firstFrames() >= 1 && first.LogicalSize.Width == 800, what: "first window");

        using var second = new Toplevel(app) { AppId = "oopshell.test.second" };
        var secondFrames = Draw.SolidColor(second, 0xFF00FF00);
        app.RunUntil(() => secondFrames() >= 1 && first.LogicalSize.Width == 400, what: "tiling resize");

        Assert.Equal(new Size(400, 600), first.LogicalSize);
        Assert.Equal(new Size(400, 600), second.LogicalSize);
        app.RunUntil(() => firstFrames() >= 2, what: "redraw after resize");
        app.RunFor(50);
        // Which side the new window lands on depends on focus, so ask sway.
        var firstRect = sway.WindowRect("oopshell.test.first");
        var secondRect = sway.WindowRect("oopshell.test.second");
        var shot = sway.Screenshot();
        Assert.Equal((255, 0, 0), shot[firstRect.X + 100, 300]);
        Assert.Equal((0, 255, 0), shot[secondRect.X + 100, 300]);
    }

    [Fact]
    public void Kill_raises_CloseRequested()
    {
        using var app = sway.Connect();
        using var window = new Toplevel(app) { AppId = "oopshell.test.close" };
        var frames = Draw.SolidColor(window, 0xFF000000);
        bool closeRequested = false;
        window.CloseRequested += () => closeRequested = true;
        app.RunUntil(() => frames() >= 1, what: "first frame");

        sway.Swaymsg("[app_id=oopshell.test.close] kill");
        app.RunUntil(() => closeRequested, what: "close request");
    }

    [Fact]
    public void Fullscreen_test_pattern_matches_golden()
    {
        using var app = sway.Connect();
        using var window = new Toplevel(app);
        window.SetFullscreen(true);
        var chain = new ShmSwapchain(window);
        int frames = 0;
        window.Render += frame =>
        {
            using var buffer = chain.Acquire(frame)!;
            Draw.TestPattern(buffer);
            buffer.Present();
            frames++;
        };
        app.RunUntil(() => frames >= 1 && window.States.HasFlag(ToplevelStates.Fullscreen) && window.LogicalSize == new Size(800, 600), what: "fullscreen frame");
        app.RunFor(100);

        sway.Screenshot().AssertMatchesGolden("toplevel-test-pattern");
    }

    [Fact]
    public void Sway_insists_on_server_side_decorations_for_tiled_windows()
    {
        using var app = sway.Connect();
        using var window = new Toplevel(app);
        window.RequestDecoration(DecorationMode.ClientSide);
        var frames = Draw.SolidColor(window, 0xFF000000);
        // sway only grants client-side decorations to floating windows; the reply arrives with a configure.
        app.RunUntil(() => frames() >= 1 && window.Decoration == DecorationMode.ServerSide, what: "server-side decoration");
    }
}
