using System.Drawing;
using oopshell.Protocol;
using static oopshell.IntegrationTests.VirtualInput;

namespace oopshell.IntegrationTests;

[Collection("sway")]
public class PopupTests(SwayFixture sway)
{
    private static PopupOptions MenuAt(int x, int y) => new()
    {
        Size = new Size(120, 80),
        AnchorRect = new Rectangle(x, y, 1, 1),
        Anchor = XdgPositionerAnchor.TopLeft,
        Gravity = XdgPositionerGravity.BottomRight,
    };

    [Fact]
    public void Nested_grabbed_popups_are_dismissed_by_a_click_on_another_client()
    {
        using var app = sway.Connect();
        using var input = new VirtualInput(app);
        input.MoveTo(1, 1);
        using var window = new Toplevel(app);
        var frames = Draw.SolidColor(window, 0xFF202020);
        app.RunUntil(() => frames() >= 1 && window.LogicalSize.Width == 800, what: "window");

        // Grabs only dismiss on clicks that land on *another* client's surface; same-client clicks are
        // delivered normally. The other client is a small overlay in the bottom-right corner.
        using var otherApp = sway.Connect();
        using var other = new LayerSurface(otherApp, otherApp.Outputs[0], Layer.Overlay, "test-other")
        {
            Anchor = Anchor.Bottom | Anchor.Right,
            Size = new Size(100, 100),
        };
        var otherFrames = Draw.SolidColor(other, 0xFF404040);
        otherApp.RunUntil(() => otherFrames() >= 1, what: "other client's surface");

        Popup? menu = null, submenu = null;
        bool menuDismissed = false, submenuDismissed = false;
        window.PointerButton += e =>
        {
            if (!e.Pressed || menu is not null)
                return;
            menu = new Popup(window, MenuAt((int)e.X, (int)e.Y));
            menu.Grab(e.Seat);
            menu.Dismissed += () => menuDismissed = true;
            Draw.SolidColor(menu, 0xFFC0C0C0);
        };
        input.MoveTo(100, 100);
        var seat = app.DefaultSeat!;
        app.RunUntil(() => seat.PointerFocus == window && seat.PointerX == 100 && seat.PointerY == 100, what: "pointer over window");
        input.Button(BTN_LEFT, true);
        app.RunUntil(() => menu is { IsConfigured: true }, what: "menu");
        input.Button(BTN_LEFT, false);
        Assert.Equal(new Rectangle(100, 100, 120, 80), menu!.Geometry);

        // A submenu to the right of the first item; nested popups grab too.
        submenu = new Popup(menu, new PopupOptions
        {
            Size = new Size(100, 60),
            AnchorRect = new Rectangle(0, 0, 120, 20),
            Anchor = XdgPositionerAnchor.TopRight,
            Gravity = XdgPositionerGravity.BottomRight,
        });
        submenu.Grab(app.DefaultSeat!);
        submenu.Dismissed += () => submenuDismissed = true;
        var subFrames = Draw.SolidColor(submenu, 0xFF000080);
        app.RunUntil(() => subFrames() >= 1, what: "submenu");
        app.RunFor(50);
        var shot = sway.Screenshot();
        Assert.Equal((0xC0, 0xC0, 0xC0), shot[150, 150]);
        Assert.Equal((0, 0, 0x80), shot[250, 130]);

        input.MoveTo(750, 550);
        input.Click();
        otherApp.RunFor(50);
        app.RunUntil(() => menuDismissed && submenuDismissed, what: "dismissal");
        submenu.Dispose();
        menu.Dispose();
        Assert.Empty(window.ChildPopups);
    }

    [Fact]
    public void Popup_from_layer_surface_and_reposition()
    {
        using var app = sway.Connect();
        using var bar = new LayerSurface(app, app.Outputs[0], Layer.Top, "test-bar")
        {
            Anchor = Anchor.Bottom | Anchor.Left | Anchor.Right,
            Size = new Size(0, 30),
            ExclusiveZone = 30,
        };
        var barFrames = Draw.SolidColor(bar, 0xFF808080);
        app.RunUntil(() => barFrames() >= 1, what: "bar");

        using var popup = new Popup(bar, new PopupOptions
        {
            Size = new Size(150, 200),
            AnchorRect = new Rectangle(0, 0, 60, 30),
            Anchor = XdgPositionerAnchor.TopLeft,
            Gravity = XdgPositionerGravity.TopRight,
        });
        var frames = Draw.SolidColor(popup, 0xFF00FF00);
        app.RunUntil(() => frames() >= 1, what: "popup");
        Assert.Equal(new Rectangle(0, -200, 150, 200), popup.Geometry);
        Assert.Equal((0, 255, 0), sway.Screenshot()[75, 470]);

        popup.Reposition(popup.Options with { AnchorRect = new Rectangle(300, 0, 60, 30) });
        app.RunUntil(() => popup.Geometry.X == 300, what: "reposition");
    }

    [Fact]
    public async Task Launched_process_receives_an_activation_token()
    {
        using var app = sway.Connect();
        string file = Path.Combine(sway.RuntimeDir, $"token-{Guid.NewGuid():N}");
        var launch = app.LaunchAsync("/bin/sh", ["-c", $"printf '%s|%s' \"$XDG_ACTIVATION_TOKEN\" \"$DESKTOP_STARTUP_ID\" > {file}"]);
        app.RunUntil(() => launch.IsCompleted && File.Exists(file) && File.ReadAllText(file).Length > 1, what: "child output");
        await launch;

        var parts = File.ReadAllText(file).Split('|');
        Assert.NotEmpty(parts[0]);
        Assert.Equal(parts[0], parts[1]);
    }
}
