using System.Text;
using oopshell.Native;
using oopshell.Protocol;

namespace oopshell.IntegrationTests;

/// <summary>
/// Injects pointer and keyboard input through <c>zwlr_virtual_pointer_v1</c> and <c>zwp_virtual_keyboard_v1</c>,
/// using oopshell's own generated bindings. The keyboard uses tests/fixtures/us-compose-ralt.xkb.
/// </summary>
public sealed class VirtualInput : IDisposable
{
    // evdev codes (linux/input-event-codes.h)
    public const uint KEY_A = 30, KEY_E = 18, KEY_APOSTROPHE = 40, KEY_RIGHTALT = 100, KEY_ESC = 1, KEY_ENTER = 28;
    public const uint BTN_LEFT = 0x110;

    readonly OopsApp app;
    readonly ZwlrVirtualPointerV1 pointer;
    readonly ZwpVirtualKeyboardV1 keyboard;
    uint time;

    public unsafe VirtualInput(OopsApp app)
    {
        this.app = app;
        var seat = app.DefaultSeat ?? throw new InvalidOperationException("No seat.");
        pointer = app.Get<ZwlrVirtualPointerManagerV1>().CreateVirtualPointer(seat.Proxy);
        keyboard = app.Get<ZwpVirtualKeyboardManagerV1>().CreateVirtualKeyboard(seat.Proxy);

        byte[] keymap = Encoding.UTF8.GetBytes(File.ReadAllText(Path.Combine(SwayFixture.RepoRoot, "tests/fixtures/us-compose-ralt.xkb")) + "\0");
        int fd;
        using (var name = new NativeUtf8("oopshell-test-keymap"))
            fd = LibC.memfd_create(name.Pointer, LibC.MFD_CLOEXEC);
        fixed (byte* p = keymap)
            LibC.write(fd, p, (nuint)keymap.Length);
        keyboard.Keymap((uint)WlKeyboardKeymapFormat.XkbV1, fd, (uint)keymap.Length);
        LibC.close(fd);
        app.Display.Roundtrip();
        app.Display.Roundtrip();
    }

    /// <summary>Moves the pointer to an absolute position in a layout of <paramref name="layoutWidth"/>×<paramref name="layoutHeight"/>.</summary>
    public void MoveTo(double x, double y, uint layoutWidth = 800, uint layoutHeight = 600)
    {
        pointer.MotionAbsolute(time++, (uint)x, (uint)y, layoutWidth, layoutHeight);
        pointer.Frame();
        app.Display.Flush();
    }

    public void Button(uint button, bool pressed)
    {
        pointer.Button(time++, button, pressed ? WlPointerButtonState.Pressed : WlPointerButtonState.Released);
        pointer.Frame();
        app.Display.Flush();
    }

    public void Click(uint button = BTN_LEFT)
    {
        Button(button, true);
        Button(button, false);
    }

    public void Scroll(double dy, int discrete)
    {
        pointer.AxisSource(WlPointerAxisSource.Wheel);
        pointer.AxisDiscrete(time++, WlPointerAxis.VerticalScroll, dy, discrete);
        pointer.Frame();
        app.Display.Flush();
    }

    public void Key(uint key, bool pressed)
    {
        keyboard.Key(time++, key, (uint)(pressed ? WlKeyboardKeyState.Pressed : WlKeyboardKeyState.Released));
        app.Display.Flush();
    }

    public void Tap(uint key)
    {
        Key(key, true);
        Key(key, false);
    }

    public void Dispose()
    {
        pointer.Dispose();
        keyboard.Dispose();
    }
}
