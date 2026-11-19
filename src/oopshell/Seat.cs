using oopshell.Native;
using oopshell.Protocol;

namespace oopshell;

/// <summary>A group of input devices (pointer, keyboard, touch). Routes their events to oopshell surfaces.</summary>
public sealed class Seat
{
    readonly OopsApp app;
    readonly KeyboardState xkb = new();
    WlPointer? pointer;
    WlKeyboard? keyboard;
    WlTouch? touch;
    WpCursorShapeDeviceV1? cursorShapeDevice;
    CursorThemeFallback? cursorFallback;

    // Pointer state, plus what has accumulated since the last wl_pointer.frame.
    uint pointerEnterSerial;
    PendingPointerFrame frame;
    readonly List<(PointerButton Button, bool Pressed, uint Time)> pendingButtons = new();

    // Key repeat.
    int repeatRate = 25;
    int repeatDelay = 600;
    IDisposable? repeatTimer;

    readonly Dictionary<int, Surface> touchPoints = new();

    internal Seat(OopsApp app, GlobalInfo global)
    {
        this.app = app;
        Global = global;
        xkb.LoadComposeFromLocale();
        Proxy = app.Bind<WlSeat>(global);
        Proxy.Name += e => Name = e.Name;
        Proxy.Capabilities += e => SetCapabilities(e.Capabilities);
    }

    internal GlobalInfo Global { get; }

    /// <summary>The raw <c>wl_seat</c> (escape hatch).</summary>
    public WlSeat Proxy { get; }

    public string Name { get; private set; } = "";

    public WlSeatCapability Capabilities { get; private set; }

    /// <summary>
    /// Serial of the latest button, key or touch event. Popup grabs, move/resize and activation tokens use it.
    /// (Enter/leave/modifier serials are deliberately excluded: a click that moves keyboard focus is followed
    /// by a <c>wl_keyboard.enter</c>, and its serial is not valid for a grab.)
    /// </summary>
    public uint LastSerial { get; private set; }

    /// <summary>The surface under the pointer, if it is one of ours.</summary>
    public Surface? PointerFocus { get; private set; }

    /// <summary>The surface with keyboard focus, if it is one of ours.</summary>
    public Surface? KeyboardFocus { get; private set; }

    public double PointerX { get; private set; }
    public double PointerY { get; private set; }

    public Modifiers Modifiers => xkb.Modifiers;

    /// <summary>The active keyboard layout's name, e.g. "English (US)".</summary>
    public string? KeymapLayoutName => xkb.LayoutName;

    /// <summary>Raised when capabilities or the keymap change.</summary>
    public event Action? Changed;

    public WlPointer? Pointer => pointer;
    public WlKeyboard? Keyboard => keyboard;

    void SetCapabilities(WlSeatCapability caps)
    {
        Capabilities = caps;
        bool hasPointer = (caps & WlSeatCapability.Pointer) != 0;
        bool hasKeyboard = (caps & WlSeatCapability.Keyboard) != 0;
        bool hasTouch = (caps & WlSeatCapability.Touch) != 0;

        if (hasPointer && pointer is null)
            SetUpPointer();
        else if (!hasPointer && pointer is not null)
        {
            cursorShapeDevice?.Dispose();
            cursorShapeDevice = null;
            pointer.Dispose();
            pointer = null;
            PointerFocus = null;
        }

        if (hasKeyboard && keyboard is null)
            SetUpKeyboard();
        else if (!hasKeyboard && keyboard is not null)
        {
            StopRepeat();
            keyboard.Dispose();
            keyboard = null;
            KeyboardFocus = null;
        }

        if (hasTouch && touch is null)
            SetUpTouch();
        else if (!hasTouch && touch is not null)
        {
            touch.Dispose();
            touch = null;
            touchPoints.Clear();
        }
        Changed?.Invoke();
    }

    // ---- pointer ------------------------------------------------------------------------

    struct PendingPointerFrame
    {
        public Surface? Enter;
        public bool Leave;
        public bool Motion;
        public bool Axis;
        public double DeltaX, DeltaY;
        public int Value120X, Value120Y;
        public WlPointerAxisSource Source;
        public bool StopX, StopY;
        public uint Time;
    }

    void SetUpPointer()
    {
        var p = pointer = Proxy.GetPointer();
        if (app.TryGet<WpCursorShapeManagerV1>() is { } shapes)
            cursorShapeDevice = shapes.GetPointer(p);
        else if (app.Options.CursorThemeFallback)
            cursorFallback ??= CursorThemeFallback.TryCreate(app);

        p.Enter += e =>
        {
            pointerEnterSerial = e.Serial;
            if (PointerFocus is not null)
                frame.Leave = true;
            frame.Enter = app.FindSurface(e.Surface);
            PointerX = e.SurfaceX;
            PointerY = e.SurfaceY;
            EndFrameIfUnframed();
        };
        p.Leave += e =>
        {
            frame.Leave = true;
            EndFrameIfUnframed();
        };
        p.Motion += e =>
        {
            PointerX = e.SurfaceX;
            PointerY = e.SurfaceY;
            frame.Motion = true;
            frame.Time = e.Time;
            EndFrameIfUnframed();
        };
        p.Button += e =>
        {
            LastSerial = e.Serial;
            pendingButtons.Add(((PointerButton)e.Button, e.State == WlPointerButtonState.Pressed, e.Time));
            EndFrameIfUnframed();
        };
        p.Axis += e =>
        {
            frame.Axis = true;
            frame.Time = e.Time;
            if (e.Axis == WlPointerAxis.VerticalScroll)
                frame.DeltaY += e.Value;
            else
                frame.DeltaX += e.Value;
            EndFrameIfUnframed();
        };
        p.AxisSource += e => frame.Source = e.AxisSource;
        p.AxisStop += e =>
        {
            frame.Axis = true;
            if (e.Axis == WlPointerAxis.VerticalScroll)
                frame.StopY = true;
            else
                frame.StopX = true;
        };
        p.AxisDiscrete += e =>
        {
            // Pre-v8 compositors send discrete steps; express them as value120.
            if (p.BoundVersion >= 8)
                return;
            if (e.Axis == WlPointerAxis.VerticalScroll)
                frame.Value120Y += e.Discrete * 120;
            else
                frame.Value120X += e.Discrete * 120;
        };
        p.AxisValue120 += e =>
        {
            if (e.Axis == WlPointerAxis.VerticalScroll)
                frame.Value120Y += e.Value120;
            else
                frame.Value120X += e.Value120;
        };
        p.Frame += FlushPointerFrame;
    }

    // wl_pointer before v5 has no frame event: every event is its own frame.
    void EndFrameIfUnframed()
    {
        if (pointer!.BoundVersion < 5)
            FlushPointerFrame();
    }

    void FlushPointerFrame()
    {
        var f = frame;
        frame = default;

        if (f.Leave && PointerFocus is { } left)
        {
            PointerFocus = null;
            left.RaisePointerLeave(new PointerEvent(this, PointerX, PointerY, f.Time));
        }
        if (f.Enter is { } entered)
        {
            PointerFocus = entered;
            ApplyCursor(entered);
            entered.RaisePointerEnter(new PointerEvent(this, PointerX, PointerY, f.Time));
        }
        if (PointerFocus is not { } target)
        {
            pendingButtons.Clear();
            return;
        }
        if (f.Motion)
            target.RaisePointerMove(new PointerEvent(this, PointerX, PointerY, f.Time));
        foreach (var (button, pressed, time) in pendingButtons)
            target.RaisePointerButton(new PointerButtonEvent(this, PointerX, PointerY, button, pressed, time));
        pendingButtons.Clear();
        if (f.Axis)
        {
            target.RaisePointerScroll(new PointerScrollEvent(this, PointerX, PointerY, f.DeltaX, f.DeltaY,
                f.Value120X, f.Value120Y, f.Source, f.StopX, f.StopY, f.Time));
        }
    }

    /// <summary>Shows <paramref name="surface"/>'s cursor if the pointer is over it.</summary>
    internal void ApplyCursor(Surface surface)
    {
        if (PointerFocus != surface || pointer is null)
            return;
        if (cursorShapeDevice is not null)
            cursorShapeDevice.SetShape(pointerEnterSerial, (WpCursorShapeDeviceV1Shape)surface.Cursor);
        else
            cursorFallback?.Apply(pointer, pointerEnterSerial, surface.Cursor, (int)Math.Ceiling(surface.Scale));
    }

    // ---- keyboard -----------------------------------------------------------------------

    unsafe void SetUpKeyboard()
    {
        var k = keyboard = Proxy.GetKeyboard();
        k.Keymap += e =>
        {
            if (e.Format != WlKeyboardKeymapFormat.XkbV1)
                return;
            // Map no more than the file holds (reading past its end is SIGBUS); a keymap that cannot be mapped
            // leaves the previous one in place rather than stopping the app.
            long fileSize = LibC.lseek(e.Fd, 0, LibC.SEEK_END);
            uint size = (uint)Math.Min(e.Size, fileSize < 0 ? 0 : fileSize);
            if (size == 0 || size > 64 * 1024 * 1024)
                return;
            nint map = LibC.mmap(0, size, LibC.PROT_READ, LibC.MAP_PRIVATE, e.Fd, 0);
            if (map == LibC.MAP_FAILED)
                return;
            try
            {
                xkb.SetKeymap(new ReadOnlySpan<byte>((void*)map, (int)size));
            }
            finally
            {
                LibC.munmap(map, size);
            }
            Changed?.Invoke();
        };
        k.Enter += e =>
        {
            KeyboardFocus = app.FindSurface(e.Surface);
            KeyboardFocus?.RaiseKeyboardFocus(new KeyboardFocusEvent(this, true));
        };
        k.Leave += e =>
        {
            StopRepeat();
            var old = KeyboardFocus;
            KeyboardFocus = null;
            old?.RaiseKeyboardFocus(new KeyboardFocusEvent(this, false));
        };
        k.Modifiers += e =>
        {
            xkb.UpdateModifiers(e.ModsDepressed, e.ModsLatched, e.ModsLocked, e.Group);
            KeyboardFocus?.RaiseModifiersChanged(new ModifiersEvent(this, xkb.Modifiers));
        };
        k.Key += e =>
        {
            LastSerial = e.Serial;
            bool pressed = e.State == WlKeyboardKeyState.Pressed;
            if (pressed)
            {
                PressKey(e.Key, e.Time, isRepeat: false);
                StartRepeat(e.Key);
            }
            else
            {
                if (repeatKey == e.Key)
                    StopRepeat();
                KeyboardFocus?.RaiseKeyUp(new KeyEvent(this, e.Key, xkb.GetKeysym(e.Key), xkb.Modifiers, false, e.Time));
            }
        };
        k.RepeatInfo += e =>
        {
            repeatRate = e.Rate;
            repeatDelay = e.Delay;
        };
    }

    void PressKey(uint key, uint time, bool isRepeat)
    {
        var target = KeyboardFocus;
        if (target is null)
            return;
        target.RaiseKeyDown(new KeyEvent(this, key, xkb.GetKeysym(key), xkb.Modifiers, isRepeat, time));
        // Repeats bypass compose: the sequence already finished (or never started) on the first press.
        if (xkb.GetText(key, useCompose: !isRepeat) is { } text)
            target.RaiseTextInput(new TextInputEvent(this, text));
    }

    uint repeatKey;

    void StartRepeat(uint key)
    {
        StopRepeat();
        if (repeatRate <= 0 || !xkb.KeyRepeats(key))
            return;
        repeatKey = key;
        uint time = 0;
        repeatTimer = app.SetTimer(TimeSpan.FromMilliseconds(repeatDelay), TimeSpan.FromMilliseconds(1000.0 / repeatRate),
            () => PressKey(key, time++, isRepeat: true));
    }

    void StopRepeat()
    {
        repeatTimer?.Dispose();
        repeatTimer = null;
        repeatKey = 0;
    }

    // ---- touch --------------------------------------------------------------------------

    void SetUpTouch()
    {
        var t = touch = Proxy.GetTouch();
        t.Down += e =>
        {
            LastSerial = e.Serial;
            if (app.FindSurface(e.Surface) is not { } surface)
                return;
            touchPoints[e.Id] = surface;
            surface.RaiseTouchDown(new TouchEvent(this, e.Id, e.X, e.Y, e.Time));
        };
        t.Motion += e =>
        {
            if (touchPoints.TryGetValue(e.Id, out var surface))
                surface.RaiseTouchMove(new TouchEvent(this, e.Id, e.X, e.Y, e.Time));
        };
        t.Up += e =>
        {
            LastSerial = e.Serial;
            if (touchPoints.Remove(e.Id, out var surface))
                surface.RaiseTouchUp(new TouchEvent(this, e.Id, 0, 0, e.Time));
        };
        t.Cancel += () =>
        {
            foreach (var (id, surface) in touchPoints)
                surface.RaiseTouchCancel(new TouchEvent(this, id, 0, 0, 0));
            touchPoints.Clear();
        };
    }

    /// <summary>Forgets a surface that is going away so no more events are routed to it.</summary>
    internal void Forget(Surface surface)
    {
        if (PointerFocus == surface)
            PointerFocus = null;
        if (KeyboardFocus == surface)
        {
            StopRepeat();
            KeyboardFocus = null;
        }
        if (frame.Enter == surface)
            frame.Enter = null;
        foreach (var id in touchPoints.Where(p => p.Value == surface).Select(p => p.Key).ToList())
            touchPoints.Remove(id);
    }

    internal void Remove()
    {
        StopRepeat();
        cursorShapeDevice?.Dispose();
        pointer?.Dispose();
        keyboard?.Dispose();
        touch?.Dispose();
        cursorFallback?.Dispose();
        xkb.Dispose();
        Proxy.Dispose();
    }

    public override string ToString() => $"{Name} ({Capabilities})";
}

public sealed partial class OopsApp
{
    readonly List<Seat> seats = new();

    public IReadOnlyList<Seat> Seats => seats;

    /// <summary>The first seat, or null on a compositor without input (rare).</summary>
    public Seat? DefaultSeat => seats.Count > 0 ? seats[0] : null;

    public event Action<Seat>? SeatAdded;
    public event Action<Seat>? SeatRemoved;

    void AddSeat(GlobalInfo global)
    {
        var seat = new Seat(this, global);
        seats.Add(seat);
        SeatAdded?.Invoke(seat);
    }

    void RemoveSeat(GlobalInfo global)
    {
        var seat = seats.Find(s => s.Global == global);
        if (seat is null)
            return;
        seats.Remove(seat);
        SeatRemoved?.Invoke(seat);
        seat.Remove();
    }
}
