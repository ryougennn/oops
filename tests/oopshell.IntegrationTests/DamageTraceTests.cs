using System.Drawing;
using System.Runtime.InteropServices;

namespace oopshell.IntegrationTests;

/// <summary>Checks the actual requests on the wire with WAYLAND_DEBUG=1 (libwayland logs them to stderr).</summary>
[Collection("sway")]
public partial class DamageTraceTests(SwayFixture sway)
{
    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int setenv(string name, string value, int overwrite);
    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int unsetenv(string name);
    [LibraryImport("libc")]
    private static partial int dup(int fd);
    [LibraryImport("libc")]
    private static partial int dup2(int fd, int fd2);

    [Fact]
    public void Layer_surface_state_changes_are_committed_at_once()
    {
        sway.RequireSway();
        string log = Path.Combine(sway.RuntimeDir, "wayland-debug-layer.log");
        int savedStderr = dup(2);
        using (var file = File.Create(log))
        {
            setenv("WAYLAND_DEBUG", "client", 1);
            dup2((int)file.SafeFileHandle.DangerousGetHandle(), 2);
            try
            {
                using var app = sway.Connect();
                using var bar = new LayerSurface(app, null, Layer.Top, "test-bar") { Anchor = Anchor.Top, Size = new Size(100, 20) };
                var frames = Draw.SolidColor(bar, 0xFF808080);
                app.RunUntil(() => frames() >= 1, what: "first frame");
                // Right after a frame (its callback may still be pending): the change must not wait for the next one.
                bar.KeyboardInteractivity = KeyboardInteractivity.OnDemand;
                app.Display.Flush();
                app.RunFor(100);
            }
            finally
            {
                dup2(savedStderr, 2);
                unsetenv("WAYLAND_DEBUG");
            }
        }
        var requests = File.ReadAllLines(log).Where(l => l.Contains(" -> ")).ToList();
        int set = requests.FindIndex(l => l.Contains(".set_keyboard_interactivity(2)"));
        Assert.True(set >= 0, string.Join("\n", requests.TakeLast(15)));
        var next = requests.Skip(set + 1).First(l => l.Contains("wl_surface#"));
        Assert.Contains(".commit()", next);
    }

    [Fact]
    public void Popup_grab_uses_the_serial_of_the_click_that_opened_it()
    {
        sway.RequireSway();
        string log = Path.Combine(sway.RuntimeDir, "wayland-debug-grab.log");
        int savedStderr = dup(2);
        using (var file = File.Create(log))
        {
            setenv("WAYLAND_DEBUG", "client", 1);
            dup2((int)file.SafeFileHandle.DangerousGetHandle(), 2);
            try
            {
                using var app = sway.Connect();
                using var input = new VirtualInput(app);
                using var window = new Toplevel(app);
                var frames = Draw.SolidColor(window, 0xFF202020);
                app.RunUntil(() => frames() >= 1 && window.LogicalSize.Width == 800, what: "window");
                input.MoveTo(100, 100);
                app.RunUntil(() => app.DefaultSeat!.PointerFocus == window, what: "pointer over window");

                Popup? menu = null;
                window.PointerButton += e =>
                {
                    if (!e.Pressed || menu is not null)
                        return;
                    menu = new Popup(window, new PopupOptions { Size = new Size(50, 50), AnchorRect = new Rectangle(100, 100, 1, 1) });
                    menu.Grab(e.Seat);
                    Draw.SolidColor(menu, 0xFFC0C0C0);
                };
                // Press and release arrive in one batch, so the release is the seat's latest serial by the
                // time the popup is first committed.
                input.Click();
                app.RunUntil(() => menu is { IsConfigured: true }, what: "menu");
                menu!.Dispose();
            }
            finally
            {
                dup2(savedStderr, 2);
                unsetenv("WAYLAND_DEBUG");
            }
        }
        string trace = File.ReadAllText(log);
        var press = PressSerial().Match(trace);
        var grab = GrabSerial().Match(trace);
        Assert.True(press.Success && grab.Success, trace);
        Assert.Equal(press.Groups[1].Value, grab.Groups[1].Value);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"wl_pointer#\d+\.button\((\d+), \d+, 272, 1\)")]
    private static partial System.Text.RegularExpressions.Regex PressSerial();

    [System.Text.RegularExpressions.GeneratedRegex(@"xdg_popup#\d+\.grab\(wl_seat#\d+, (\d+)\)")]
    private static partial System.Text.RegularExpressions.Regex GrabSerial();

    [Fact]
    public void Partial_invalidation_sends_partial_damage_buffer()
    {
        sway.RequireSway();
        string log = Path.Combine(sway.RuntimeDir, "wayland-debug.log");
        int savedStderr = dup(2);
        using (var file = File.Create(log))
        {
            // libwayland reads WAYLAND_DEBUG from the C environment at connect time and logs to fd 2.
            setenv("WAYLAND_DEBUG", "client", 1);
            dup2((int)file.SafeFileHandle.DangerousGetHandle(), 2);
            try
            {
                using var app = sway.Connect();
                using var window = new Toplevel(app);
                var frames = Draw.SolidColor(window, 0xFF000000);
                app.RunUntil(() => frames() >= 1 && window.LogicalSize.Width == 800, what: "first frame");
                int before = frames();
                window.Invalidate(new Rectangle(10, 20, 30, 40));
                app.RunUntil(() => frames() > before, what: "partial frame");

                // The swapchain was asked to present the whole buffer by Draw.SolidColor; present the frame
                // damage instead to see it on the wire.
                var chain = new ShmSwapchain(window);
                window.Render += frame =>
                {
                    using var buffer = chain.Acquire(frame);
                    buffer?.Present(frame.Damage);
                };
                window.Invalidate(new Rectangle(10, 20, 30, 40));
                app.RunFor(200);
                chain.Dispose();
            }
            finally
            {
                dup2(savedStderr, 2);
                unsetenv("WAYLAND_DEBUG");
            }
        }
        string trace = File.ReadAllText(log);
        Assert.Contains("wl_surface#", trace);
        Assert.Contains(".damage_buffer(10, 20, 30, 40)", trace);
    }
}
