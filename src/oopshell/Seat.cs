using oopshell.Native;
using oopshell.Protocol;

namespace oopshell;

/// <summary>A group of input devices (pointer, keyboard, touch). Routes their events to oopshell surfaces.</summary>
public sealed class Seat
{
    private readonly OopsApp _app;
    private readonly KeyboardState _xkb = new();
    private WlPointer? _pointer;
    private WlKeyboard? _keyboard;
    private WlTouch? _touch;
    private WpCursorShapeDeviceV1? _cursorShapeDevice;
    private CursorThemeFallback? _cursorFallback;

    private readonly List<(PointerButton Button, bool Pressed, uint Time)> _pendingButtons = new();
    private readonly Dictionary<int, Surface> _touchPoints = new();
    private uint _pointerEnterSerial;
    private PendingPointerFrame _frame; // accumulated since the last wl_pointer.frame

    private int _repeatRate = 25;   // keys per second
    private int _repeatDelay = 600; // milliseconds
    private uint _repeatKey;
    private IDisposable? _repeatTimer;

    internal Seat(OopsApp app, GlobalInfo global)
    {
        _app = app;
        Global = global;
        _xkb.LoadComposeFromLocale();
        Proxy = app.Bind<WlSeat>(global);
        Proxy.Name += e => Name = e.Name;
        Proxy.Capabilities += e => SetCapabilities(e.Capabilities);
    }

    internal GlobalInfo Global { get; }

    /// <summary>The raw <c>wl_seat</c> (escape hatch).</summary>
    public WlSeat Proxy { get; }

    public string Name { get; private set; } = "";

    public WlSeatCapability Capabilities { get; private set; }

    /// <summary>Serial of the latest button, key or touch event; used for popup grabs, move/resize and activation tokens.</summary>
    /// <remarks>
    /// Enter, leave and modifier serials are excluded: a click that moves keyboard focus is followed by a
    /// <c>wl_keyboard.enter</c> whose serial is not valid for a grab.
    /// </remarks>
    public uint LastSerial { get; private set; }

    /// <summary>The surface under the pointer, if it is one of ours.</summary>
    public Surface? PointerFocus { get; private set; }

    /// <summary>The surface with keyboard focus, if it is one of ours.</summary>
    public Surface? KeyboardFocus { get; private set; }

    /// <summary>Last pointer position on <see cref="PointerFocus"/>, in its logical surface coordinates.</summary>
    public double PointerX { get; private set; }
    /// <summary>Last pointer position on <see cref="PointerFocus"/>, in its logical surface coordinates.</summary>
    public double PointerY { get; private set; }

    /// <summary>Modifiers currently held or locked on this seat's keyboard.</summary>
    public Modifiers Modifiers => _xkb.Modifiers;

    /// <summary>The active keyboard layout's name, e.g. "English (US)".</summary>
    public string? KeymapLayoutName => _xkb.LayoutName;

    /// <summary>Raised when capabilities or the keymap change.</summary>
    public event Action? Changed;

    public WlPointer? Pointer => _pointer;
    public WlKeyboard? Keyboard => _keyboard;

    private void SetCapabilities(WlSeatCapability caps)
    {
        Capabilities = caps;
        bool hasPointer = (caps & WlSeatCapability.Pointer) != 0;
        bool hasKeyboard = (caps & WlSeatCapability.Keyboard) != 0;
        bool hasTouch = (caps & WlSeatCapability.Touch) != 0;

        if (hasPointer && _pointer is null)
            SetUpPointer();
        else if (!hasPointer && _pointer is not null)
        {
            _cursorShapeDevice?.Dispose();
            _cursorShapeDevice = null;
            _pointer.Dispose();
            _pointer = null;
            PointerFocus = null;
        }

        if (hasKeyboard && _keyboard is null)
            SetUpKeyboard();
        else if (!hasKeyboard && _keyboard is not null)
        {
            StopRepeat();
            _keyboard.Dispose();
            _keyboard = null;
            KeyboardFocus = null;
        }

        if (hasTouch && _touch is null)
            SetUpTouch();
        else if (!hasTouch && _touch is not null)
        {
            _touch.Dispose();
            _touch = null;
            _touchPoints.Clear();
        }
        Changed?.Invoke();
    }

    private struct PendingPointerFrame
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

    private void SetUpPointer()
    {
        var p = _pointer = Proxy.GetPointer();
        if (_app.TryGet<WpCursorShapeManagerV1>() is { } shapes)
            _cursorShapeDevice = shapes.GetPointer(p);
        else if (_app.Options.CursorThemeFallback)
            _cursorFallback ??= CursorThemeFallback.TryCreate(_app);

        p.Enter += e =>
        {
            _pointerEnterSerial = e.Serial;
            if (PointerFocus is not null)
                _frame.Leave = true;
            _frame.Enter = _app.FindSurface(e.Surface);
            PointerX = e.SurfaceX;
            PointerY = e.SurfaceY;
            EndFrameIfUnframed();
        };
        p.Leave += e =>
        {
            _frame.Leave = true;
            EndFrameIfUnframed();
        };
        p.Motion += e =>
        {
            PointerX = e.SurfaceX;
            PointerY = e.SurfaceY;
            _frame.Motion = true;
            _frame.Time = e.Time;
            EndFrameIfUnframed();
        };
        p.Button += e =>
        {
            LastSerial = e.Serial;
            _pendingButtons.Add(((PointerButton)e.Button, e.State == WlPointerButtonState.Pressed, e.Time));
            EndFrameIfUnframed();
        };
        p.Axis += e =>
        {
            _frame.Axis = true;
            _frame.Time = e.Time;
            if (e.Axis == WlPointerAxis.VerticalScroll)
                _frame.DeltaY += e.Value;
            else
                _frame.DeltaX += e.Value;
            EndFrameIfUnframed();
        };
        p.AxisSource += e => _frame.Source = e.AxisSource;
        p.AxisStop += e =>
        {
            _frame.Axis = true;
            if (e.Axis == WlPointerAxis.VerticalScroll)
                _frame.StopY = true;
            else
                _frame.StopX = true;
        };
        p.AxisDiscrete += e =>
        {
            // Before v8, discrete steps arrive here instead of axis_value120.
            if (p.BoundVersion >= 8)
                return;
            if (e.Axis == WlPointerAxis.VerticalScroll)
                _frame.Value120Y += e.Discrete * 120;
            else
                _frame.Value120X += e.Discrete * 120;
        };
        p.AxisValue120 += e =>
        {
            if (e.Axis == WlPointerAxis.VerticalScroll)
                _frame.Value120Y += e.Value120;
            else
                _frame.Value120X += e.Value120;
        };
        p.Frame += FlushPointerFrame;
    }

    // wl_pointer before v5 has no frame event: every event is its own frame.
    private void EndFrameIfUnframed()
    {
        if (_pointer!.BoundVersion < 5)
            FlushPointerFrame();
    }

    private void FlushPointerFrame()
    {
        var f = _frame;
        _frame = default;

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
            _pendingButtons.Clear();
            return;
        }
        if (f.Motion)
            target.RaisePointerMove(new PointerEvent(this, PointerX, PointerY, f.Time));
        foreach (var (button, pressed, time) in _pendingButtons)
            target.RaisePointerButton(new PointerButtonEvent(this, PointerX, PointerY, button, pressed, time));
        _pendingButtons.Clear();
        if (f.Axis)
        {
            target.RaisePointerScroll(new PointerScrollEvent(this, PointerX, PointerY, f.DeltaX, f.DeltaY,
                f.Value120X, f.Value120Y, f.Source, f.StopX, f.StopY, f.Time));
        }
    }

    /// <summary>Shows <paramref name="surface"/>'s cursor if the pointer is over it.</summary>
    internal void ApplyCursor(Surface surface)
    {
        if (PointerFocus != surface || _pointer is null)
            return;
        if (_cursorShapeDevice is not null)
            _cursorShapeDevice.SetShape(_pointerEnterSerial, (WpCursorShapeDeviceV1Shape)surface.Cursor);
        else
            _cursorFallback?.Apply(_pointer, _pointerEnterSerial, surface.Cursor, (int)Math.Ceiling(surface.Scale));
    }

    private unsafe void SetUpKeyboard()
    {
        var k = _keyboard = Proxy.GetKeyboard();
        k.Keymap += e =>
        {
            if (e.Format != WlKeyboardKeymapFormat.XkbV1)
                return;
            // Map no more than the file holds: reading past its end raises SIGBUS.
            // A keymap that cannot be mapped leaves the previous one in place.
            long fileSize = LibC.lseek(e.Fd, 0, LibC.SEEK_END);
            uint size = (uint)Math.Min(e.Size, fileSize < 0 ? 0 : fileSize);
            if (size == 0 || size > 64 * 1024 * 1024)
                return;
            nint map = LibC.mmap(0, size, LibC.PROT_READ, LibC.MAP_PRIVATE, e.Fd, 0);
            if (map == LibC.MAP_FAILED)
                return;
            try
            {
                _xkb.SetKeymap(new ReadOnlySpan<byte>((void*)map, (int)size));
            }
            finally
            {
                LibC.munmap(map, size);
            }
            Changed?.Invoke();
        };
        k.Enter += e =>
        {
            KeyboardFocus = _app.FindSurface(e.Surface);
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
            _xkb.UpdateModifiers(e.ModsDepressed, e.ModsLatched, e.ModsLocked, e.Group);
            KeyboardFocus?.RaiseModifiersChanged(new ModifiersEvent(this, _xkb.Modifiers));
        };
        k.Key += e =>
        {
            LastSerial = e.Serial;
            if (e.State == WlKeyboardKeyState.Pressed)
            {
                PressKey(e.Key, e.Time, isRepeat: false);
                StartRepeat(e.Key, e.Time);
            }
            else
            {
                if (_repeatKey == e.Key)
                    StopRepeat();
                KeyboardFocus?.RaiseKeyUp(new KeyEvent(this, e.Key, _xkb.GetKeysym(e.Key), _xkb.Modifiers, false, e.Time));
            }
        };
        k.RepeatInfo += e =>
        {
            _repeatRate = e.Rate;
            _repeatDelay = e.Delay;
        };
    }

    private void PressKey(uint key, uint time, bool isRepeat)
    {
        var target = KeyboardFocus;
        if (target is null)
            return;
        target.RaiseKeyDown(new KeyEvent(this, key, _xkb.GetKeysym(key), _xkb.Modifiers, isRepeat, time));
        // Repeats bypass compose: the first press already finished or skipped the sequence.
        if (_xkb.GetText(key, useCompose: !isRepeat) is { } text)
            target.RaiseTextInput(new TextInputEvent(this, text));
    }

    private void StartRepeat(uint key, uint pressTime)
    {
        StopRepeat();
        if (_repeatRate <= 0 || !_xkb.KeyRepeats(key))
            return;
        _repeatKey = key;
        // Synthetic repeats carry timestamps on the compositor's clock, continuing from the press.
        uint interval = (uint)Math.Max(1, 1000 / _repeatRate);
        uint time = pressTime + (uint)_repeatDelay;
        _repeatTimer = _app.SetTimer(TimeSpan.FromMilliseconds(_repeatDelay), TimeSpan.FromMilliseconds(1000.0 / _repeatRate), () =>
        {
            PressKey(key, time, isRepeat: true);
            time += interval;
        });
    }

    private void StopRepeat()
    {
        _repeatTimer?.Dispose();
        _repeatTimer = null;
        _repeatKey = 0;
    }

    private void SetUpTouch()
    {
        var t = _touch = Proxy.GetTouch();
        t.Down += e =>
        {
            LastSerial = e.Serial;
            if (_app.FindSurface(e.Surface) is not { } surface)
                return;
            _touchPoints[e.Id] = surface;
            surface.RaiseTouchDown(new TouchEvent(this, e.Id, e.X, e.Y, e.Time));
        };
        t.Motion += e =>
        {
            if (_touchPoints.TryGetValue(e.Id, out var surface))
                surface.RaiseTouchMove(new TouchEvent(this, e.Id, e.X, e.Y, e.Time));
        };
        t.Up += e =>
        {
            LastSerial = e.Serial;
            if (_touchPoints.Remove(e.Id, out var surface))
                surface.RaiseTouchUp(new TouchEvent(this, e.Id, 0, 0, e.Time));
        };
        t.Cancel += () =>
        {
            foreach (var (id, surface) in _touchPoints)
                surface.RaiseTouchCancel(new TouchEvent(this, id, 0, 0, 0));
            _touchPoints.Clear();
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
        if (_frame.Enter == surface)
            _frame.Enter = null;
        foreach (var id in _touchPoints.Where(p => p.Value == surface).Select(p => p.Key).ToList())
            _touchPoints.Remove(id);
    }

    internal void Remove()
    {
        StopRepeat();
        _cursorShapeDevice?.Dispose();
        _pointer?.Dispose();
        _keyboard?.Dispose();
        _touch?.Dispose();
        _cursorFallback?.Dispose();
        _xkb.Dispose();
        Proxy.Dispose();
    }

    public override string ToString() => $"{Name} ({Capabilities})";
}

public sealed partial class OopsApp
{
    private readonly List<Seat> _seats = new();

    public IReadOnlyList<Seat> Seats => _seats;

    /// <summary>The first seat, or null on a compositor without input (rare).</summary>
    public Seat? DefaultSeat => _seats.Count > 0 ? _seats[0] : null;

    public event Action<Seat>? SeatAdded;
    public event Action<Seat>? SeatRemoved;

    private void AddSeat(GlobalInfo global)
    {
        var seat = new Seat(this, global);
        _seats.Add(seat);
        SeatAdded?.Invoke(seat);
    }

    private void RemoveSeat(GlobalInfo global)
    {
        var seat = _seats.Find(s => s.Global == global);
        if (seat is null)
            return;
        _seats.Remove(seat);
        SeatRemoved?.Invoke(seat);
        seat.Remove();
    }
}
