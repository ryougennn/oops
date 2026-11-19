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

    private readonly OopsApp _app;
    private readonly ZwlrVirtualPointerV1 _pointer;
    private readonly ZwpVirtualKeyboardV1 _keyboard;
    private uint _time;

    public unsafe VirtualInput(OopsApp app)
    {
        _app = app;
        var seat = app.DefaultSeat ?? throw new InvalidOperationException("No seat.");
        _pointer = app.Get<ZwlrVirtualPointerManagerV1>().CreateVirtualPointer(seat.Proxy);
        _keyboard = app.Get<ZwpVirtualKeyboardManagerV1>().CreateVirtualKeyboard(seat.Proxy);

        byte[] keymap = Encoding.UTF8.GetBytes(File.ReadAllText(Path.Combine(SwayFixture.RepoRoot, "tests/fixtures/us-compose-ralt.xkb")) + "\0");
        int fd;
        using (var name = new NativeUtf8("oopshell-test-keymap"))
            fd = LibC.memfd_create(name.Pointer, LibC.MFD_CLOEXEC);
        fixed (byte* p = keymap)
            LibC.write(fd, p, (nuint)keymap.Length);
        _keyboard.Keymap((uint)WlKeyboardKeymapFormat.XkbV1, fd, (uint)keymap.Length);
        LibC.close(fd);
        app.Display.Roundtrip();
        app.Display.Roundtrip();
    }

    /// <summary>Moves the pointer to an absolute position in a layout of <paramref name="layoutWidth"/>×<paramref name="layoutHeight"/>.</summary>
    public void MoveTo(double x, double y, uint layoutWidth = 800, uint layoutHeight = 600)
    {
        _pointer.MotionAbsolute(_time++, (uint)x, (uint)y, layoutWidth, layoutHeight);
        _pointer.Frame();
        _app.Display.Flush();
    }

    public void Button(uint button, bool pressed)
    {
        _pointer.Button(_time++, button, pressed ? WlPointerButtonState.Pressed : WlPointerButtonState.Released);
        _pointer.Frame();
        _app.Display.Flush();
    }

    public void Click(uint button = BTN_LEFT)
    {
        Button(button, true);
        Button(button, false);
    }

    public void Scroll(double dy, int discrete)
    {
        _pointer.AxisSource(WlPointerAxisSource.Wheel);
        _pointer.AxisDiscrete(_time++, WlPointerAxis.VerticalScroll, dy, discrete);
        _pointer.Frame();
        _app.Display.Flush();
    }

    public void Key(uint key, bool pressed)
    {
        _keyboard.Key(_time++, key, (uint)(pressed ? WlKeyboardKeyState.Pressed : WlKeyboardKeyState.Released));
        _app.Display.Flush();
    }

    public void Tap(uint key)
    {
        Key(key, true);
        Key(key, false);
    }

    public void Dispose()
    {
        _pointer.Dispose();
        _keyboard.Dispose();
    }
}
