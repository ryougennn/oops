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

/// <summary>Pointer entered, left, or moved over a surface.</summary>
/// <param name="Seat">The seat whose pointer it is.</param>
/// <param name="X">Surface-local position in logical pixels (left edge 0).</param>
/// <param name="Y">Surface-local position in logical pixels (top edge 0).</param>
/// <param name="Time">Milliseconds from an arbitrary base (compare events; it is not wall-clock time).</param>
public readonly record struct PointerEvent(Seat Seat, double X, double Y, uint Time);

/// <summary>A pointer button went down or up over a surface.</summary>
/// <param name="Seat">The seat whose pointer it is.</param>
/// <param name="X">Surface-local position in logical pixels.</param>
/// <param name="Y">Surface-local position in logical pixels.</param>
/// <param name="Button">The evdev button code.</param>
/// <param name="Pressed">True when pressed, false when released.</param>
/// <param name="Time">Milliseconds from an arbitrary base.</param>
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

/// <summary>A surface gained (<paramref name="Focused"/>) or lost the seat's keyboard.</summary>
/// <param name="Seat">The seat.</param>
/// <param name="Focused">True when the surface gained keyboard focus.</param>
public readonly record struct KeyboardFocusEvent(Seat Seat, bool Focused);

/// <summary>The held modifiers changed (also while no key event arrives, e.g. pressing Shift alone).</summary>
/// <param name="Seat">The seat.</param>
/// <param name="Modifiers">The modifiers now held or locked.</param>
public readonly record struct ModifiersEvent(Seat Seat, Modifiers Modifiers);

/// <summary>A touch point went down, moved, or up.</summary>
/// <param name="Seat">The seat.</param>
/// <param name="Id">Identifies the touch point from down to up (several fingers have different ids).</param>
/// <param name="X">Surface-local position in logical pixels.</param>
/// <param name="Y">Surface-local position in logical pixels.</param>
/// <param name="Time">Milliseconds from an arbitrary base.</param>
public readonly record struct TouchEvent(Seat Seat, int Id, double X, double Y, uint Time);

/// <summary>Cursor shapes from <c>wp_cursor_shape_v1</c> (CSS cursor names).</summary>
public enum CursorShape : uint
{
    Default = 1, ContextMenu, Help, Pointer, Progress, Wait, Cell, Crosshair, Text, VerticalText, Alias, Copy,
    Move, NoDrop, NotAllowed, Grab, Grabbing, EResize, NResize, NeResize, NwResize, SResize, SeResize, SwResize,
    WResize, EwResize, NsResize, NeswResize, NwseResize, ColResize, RowResize, AllScroll, ZoomIn, ZoomOut,
}
