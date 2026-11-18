using oopshell.Protocol;

namespace oopshell;

/// <summary>Common evdev pointer button codes (<c>linux/input-event-codes.h</c>). Other codes are valid too.</summary>
public enum PointerButton : uint
{
    Left = 0x110,
    Right = 0x111,
    Middle = 0x112,
    Side = 0x113,
    Extra = 0x114,
}

/// <summary>Pointer entered, left, or moved over a surface. Coordinates are logical surface pixels.</summary>
public readonly record struct PointerEvent(Seat Seat, double X, double Y, uint Time);

public readonly record struct PointerButtonEvent(Seat Seat, double X, double Y, PointerButton Button, bool Pressed, uint Time);

/// <summary>
/// All scrolling from one <c>wl_pointer.frame</c>. Deltas are continuous (logical pixels); Value120 is the
/// high-resolution wheel value (120 per detent). Stop flags mark the end of a kinetic/finger scroll.
/// </summary>
public readonly record struct PointerScrollEvent(
    Seat Seat, double X, double Y,
    double DeltaX, double DeltaY,
    int Value120X, int Value120Y,
    WlPointerAxisSource Source,
    bool StopX, bool StopY,
    uint Time);

/// <summary>A key press, repeat, or release. <see cref="Keycode"/> is the evdev code.</summary>
public readonly record struct KeyEvent(Seat Seat, uint Keycode, Keysym Keysym, Modifiers Modifiers, bool IsRepeat, uint Time);

/// <summary>Text produced by typing, after compose sequences.</summary>
public readonly record struct TextInputEvent(Seat Seat, string Text);

public readonly record struct KeyboardFocusEvent(Seat Seat, bool Focused);

public readonly record struct ModifiersEvent(Seat Seat, Modifiers Modifiers);

public readonly record struct TouchEvent(Seat Seat, int Id, double X, double Y, uint Time);

/// <summary>Cursor shapes from <c>wp_cursor_shape_v1</c> (CSS cursor names).</summary>
public enum CursorShape : uint
{
    Default = 1, ContextMenu, Help, Pointer, Progress, Wait, Cell, Crosshair, Text, VerticalText, Alias, Copy,
    Move, NoDrop, NotAllowed, Grab, Grabbing, EResize, NResize, NeResize, NwResize, SResize, SeResize, SwResize,
    WResize, EwResize, NsResize, NeswResize, NwseResize, ColResize, RowResize, AllScroll, ZoomIn, ZoomOut,
}
