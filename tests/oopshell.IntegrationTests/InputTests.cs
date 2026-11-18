using System.Drawing;
using static oopshell.IntegrationTests.VirtualInput;

namespace oopshell.IntegrationTests;

[Collection("sway")]
public class InputTests(SwayFixture sway)
{
    static (OopsApp App, VirtualInput Input, Toplevel Window) Start(SwayFixture sway)
    {
        // The compose table comes from the locale; pin it so the test does not depend on the machine.
        Environment.SetEnvironmentVariable("LC_ALL", "en_US.UTF-8");
        var app = sway.Connect();
        var input = new VirtualInput(app);
        var window = new Toplevel(app) { AppId = "oopshell.test.input" };
        var frames = Draw.SolidColor(window, 0xFF000000);
        app.RunUntil(() => frames() >= 1 && window.LogicalSize.Width == 800, what: "window");
        return (app, input, window);
    }

    [Fact]
    public void Pointer_enter_motion_and_button_reach_the_surface()
    {
        var (app, input, window) = Start(sway);
        using (app)
        using (input)
        using (window)
        {
            var events = new List<string>();
            window.PointerEnter += e => events.Add($"enter {e.X:0},{e.Y:0}");
            window.PointerMove += e => events.Add($"move {e.X:0},{e.Y:0}");
            window.PointerButton += e => events.Add($"button {e.Button} {(e.Pressed ? "down" : "up")} {e.X:0},{e.Y:0}");

            // The pointer may already be over the new window (left there by another test), so the enter
            // can carry an older position; what matters is that it arrives before the motion.
            input.MoveTo(100, 100);
            app.RunUntil(() => events.Any(e => e.StartsWith("enter")), what: "enter");
            input.MoveTo(200, 150);
            app.RunUntil(() => events.Contains("move 200,150"), what: "motion");
            input.Click();
            app.RunUntil(() => events.Contains("button Left up 200,150"), what: "click");

            Assert.Contains("button Left down 200,150", events);
            var seat = app.DefaultSeat!;
            Assert.Same(window, seat.PointerFocus);
            Assert.NotEqual(0u, seat.LastSerial);
        }
    }

    [Fact]
    public void Scroll_is_coalesced_per_frame_with_value120()
    {
        var (app, input, window) = Start(sway);
        using (app)
        using (input)
        using (window)
        {
            var scrolls = new List<PointerScrollEvent>();
            window.PointerScroll += scrolls.Add;
            input.MoveTo(300, 300);
            input.Scroll(15, 1);
            app.RunUntil(() => scrolls.Count >= 1, what: "scroll");
            app.RunFor(50);

            var scroll = Assert.Single(scrolls);
            Assert.Equal(15, scroll.DeltaY, precision: 3);
            Assert.Equal(120, scroll.Value120Y);
            Assert.Equal(oopshell.Protocol.WlPointerAxisSource.Wheel, scroll.Source);
        }
    }

    [Fact]
    public void Keys_produce_keysyms_and_text_including_compose()
    {
        var (app, input, window) = Start(sway);
        using (app)
        using (input)
        using (window)
        {
            var text = new List<string>();
            var keys = new List<Keysym>();
            window.TextInput += e => text.Add(e.Text);
            window.KeyDown += e => keys.Add(e.Keysym);
            app.RunUntil(() => app.DefaultSeat!.KeyboardFocus == window, what: "keyboard focus");

            input.Tap(KEY_A);
            app.RunUntil(() => text.Contains("a"), what: "a");

            input.Tap(KEY_RIGHTALT); // Multi_key in the test keymap
            input.Tap(KEY_APOSTROPHE);
            input.Tap(KEY_E);
            app.RunUntil(() => text.Contains("é"), what: "é via compose");

            Assert.Equal(["a", "é"], text);
            Assert.Equal([Keysym.a, Keysym.Multi_key, Keysym.apostrophe, Keysym.e], keys);
        }
    }

    [Fact]
    public void Held_key_repeats_and_stops_on_release()
    {
        var (app, input, window) = Start(sway);
        using (app)
        using (input)
        using (window)
        {
            int repeats = 0, presses = 0;
            window.KeyDown += e =>
            {
                if (e.IsRepeat)
                    repeats++;
                else
                    presses++;
            };
            app.RunUntil(() => app.DefaultSeat!.KeyboardFocus == window, what: "keyboard focus");
            sway.Swaymsg("seat", "seat0", "keyboard_grouping", "none");

            input.Key(KEY_A, true);
            // sway's default repeat: 600 ms delay, 25/s.
            app.RunUntil(() => repeats >= 3, timeoutMs: 3000, what: "repeats");
            input.Key(KEY_A, false);
            app.RunFor(100);
            int afterRelease = repeats;
            app.RunFor(300);

            Assert.Equal(1, presses);
            Assert.Equal(afterRelease, repeats);
        }
    }

    [Fact]
    public void Cursor_shape_is_applied_on_enter()
    {
        var (app, input, window) = Start(sway);
        using (app)
        using (input)
        using (window)
        {
            window.Cursor = CursorShape.Pointer;
            bool entered = false;
            window.PointerEnter += _ => entered = true;
            input.MoveTo(50, 60);
            app.RunUntil(() => entered, what: "enter");
            // No protocol error after set_shape: a roundtrip proves the connection is healthy.
            app.Display.Roundtrip();
            window.Cursor = CursorShape.Text;
            app.Display.Roundtrip();
        }
    }
}
