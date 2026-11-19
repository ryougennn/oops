using System.Text;
using oopshell.Native;
using oopshell.Protocol;
using static oopshell.Native.WaylandCursor;

namespace oopshell;

/// <summary>
/// Sets cursors from an XCursor theme via libwayland-cursor, for compositors without <c>wp_cursor_shape_v1</c>.
/// Only the first frame of animated cursors is shown.
/// </summary>
internal sealed unsafe class CursorThemeFallback : IDisposable
{
    private readonly OopsApp _app;
    private readonly WlSurface _cursorSurface;
    private readonly Dictionary<int, nint> _themes = new();
    // The theme owns the buffers; each is wrapped once for attach and the wrapper is never disposed.
    private readonly Dictionary<nint, WlBuffer> _buffers = new();

    private CursorThemeFallback(OopsApp app)
    {
        _app = app;
        _cursorSurface = app.Compositor.CreateSurface();
    }

    public static CursorThemeFallback? TryCreate(OopsApp app) => IsAvailable ? new CursorThemeFallback(app) : null;

    public void Apply(WlPointer pointer, uint serial, CursorShape shape, int scale)
    {
        nint theme = Theme(scale);
        if (theme == 0)
            return; // no theme could be loaded: keep the compositor's default cursor
        Cursor* cursor = null;
        foreach (var name in CandidateNames(shape))
        {
            using var nameUtf8 = new NativeUtf8(name);
            cursor = wl_cursor_theme_get_cursor(theme, nameUtf8.Pointer);
            if (cursor != null)
                break;
        }
        if (cursor == null || cursor->ImageCount == 0)
            return;

        CursorImage* image = cursor->Images[0];
        nint bufferHandle = wl_cursor_image_get_buffer(image);
        if (!_buffers.TryGetValue(bufferHandle, out var buffer))
            _buffers[bufferHandle] = buffer = WlBuffer.Wrap(bufferHandle);

        if (_cursorSurface.BoundVersion >= 3)
            _cursorSurface.SetBufferScale(scale);
        _cursorSurface.Attach(buffer, 0, 0);
        _cursorSurface.Damage(0, 0, int.MaxValue, int.MaxValue);
        _cursorSurface.Commit();
        pointer.SetCursor(serial, _cursorSurface, (int)image->HotspotX / scale, (int)image->HotspotY / scale);
    }

    private nint Theme(int scale)
    {
        if (_themes.TryGetValue(scale, out var theme))
            return theme;
        string? name = Environment.GetEnvironmentVariable("XCURSOR_THEME");
        int size = int.TryParse(Environment.GetEnvironmentVariable("XCURSOR_SIZE"), out int s) && s is > 0 and <= 256 ? s : 24;
        using var nameUtf8 = new NativeUtf8(name);
        theme = wl_cursor_theme_load(nameUtf8.Pointer, size * scale, _app.Shm.Handle);
        _themes[scale] = theme;
        return theme;
    }

    /// <summary>The CSS name (e.g. <c>ne-resize</c>), then the legacy X11 name some themes still use.</summary>
    private static IEnumerable<string> CandidateNames(CursorShape shape)
    {
        var css = new StringBuilder();
        foreach (char c in shape.ToString())
        {
            if (char.IsUpper(c) && css.Length > 0)
                css.Append('-');
            css.Append(char.ToLowerInvariant(c));
        }
        yield return css.ToString();
        string? legacy = shape switch
        {
            CursorShape.Default => "left_ptr",
            CursorShape.Pointer => "hand2",
            CursorShape.Text => "xterm",
            CursorShape.Wait => "watch",
            CursorShape.Move => "fleur",
            CursorShape.Crosshair => "crosshair",
            CursorShape.EwResize => "sb_h_double_arrow",
            CursorShape.NsResize => "sb_v_double_arrow",
            _ => null,
        };
        if (legacy is not null)
            yield return legacy;
        yield return "left_ptr";
    }

    public void Dispose()
    {
        _cursorSurface.Dispose();
        foreach (var theme in _themes.Values)
            wl_cursor_theme_destroy(theme);
        _themes.Clear();
    }
}
