using System.Drawing;
using oopshell.Protocol;

namespace oopshell;

/// <summary>Window state reported by the compositor.</summary>
[Flags]
public enum ToplevelStates
{
    None = 0,
    Maximized = 1 << 0,
    Fullscreen = 1 << 1,
    Resizing = 1 << 2,
    Activated = 1 << 3,
    TiledLeft = 1 << 4,
    TiledRight = 1 << 5,
    TiledTop = 1 << 6,
    TiledBottom = 1 << 7,
    Suspended = 1 << 8,
}

/// <summary>Who draws the window decorations. Values match <c>zxdg_toplevel_decoration_v1.mode</c>.</summary>
public enum DecorationMode
{
    ClientSide = 1,
    ServerSide = 2,
}

/// <summary>A regular application window (<c>xdg_toplevel</c>).</summary>
public sealed class Toplevel : Surface
{
    readonly ZxdgToplevelDecorationV1? decoration;
    Size pendingSize;
    ToplevelStates pendingStates;
    string title = "";
    string appId = "";
    Size minSize;
    Size maxSize;

    public Toplevel(OopsApp app) : base(app)
    {
        XdgSurface = app.WmBase.GetXdgSurface(Proxy);
        XdgSurface.Configure += e =>
        {
            XdgSurface.AckConfigure(e.Serial);
            States = pendingStates;
            ApplyConfigure(pendingSize.Width > 0 && pendingSize.Height > 0 ? pendingSize : LogicalSize.IsEmpty ? DefaultSize : LogicalSize);
        };
        Role = XdgSurface.GetToplevel();
        Role.Configure += e =>
        {
            pendingSize = new Size(e.Width, e.Height);
            pendingStates = ParseStates(e.States);
        };
        Role.Close += () => CloseRequested?.Invoke();
        Role.ConfigureBounds += e => Bounds = new Size(e.Width, e.Height);

        if (app.TryGet<ZxdgDecorationManagerV1>() is { } decorations)
        {
            decoration = decorations.GetToplevelDecoration(Role);
            decoration.Configure += e => Decoration = (DecorationMode)e.Mode;
        }
    }

    /// <summary>The raw <c>xdg_surface</c> (escape hatch).</summary>
    public XdgSurface XdgSurface { get; }

    /// <summary>The raw <c>xdg_toplevel</c> (escape hatch).</summary>
    public XdgToplevel Role { get; }

    /// <summary>Size to use when the compositor lets the client choose (configure with 0×0).</summary>
    public Size DefaultSize { get; set; } = new(640, 480);

    public ToplevelStates States { get; private set; }

    /// <summary>The largest size the compositor suggests (e.g. the output's usable area), if it said.</summary>
    public Size? Bounds { get; private set; }

    /// <summary>The decoration mode in effect. Client-side when the compositor lacks xdg-decoration.</summary>
    public DecorationMode Decoration { get; private set; } = DecorationMode.ClientSide;

    /// <summary>The user (or compositor) asked to close the window. oopshell does not close it for you.</summary>
    public event Action? CloseRequested;

    public string Title
    {
        get => title;
        set { title = value; Role.SetTitle(value); }
    }

    public string AppId
    {
        get => appId;
        set { appId = value; Role.SetAppId(value); }
    }

    /// <summary>Minimum logical size; empty = no minimum.</summary>
    public Size MinSize
    {
        get => minSize;
        set { minSize = value; Role.SetMinSize(value.Width, value.Height); }
    }

    /// <summary>Maximum logical size; empty = no maximum.</summary>
    public Size MaxSize
    {
        get => maxSize;
        set { maxSize = value; Role.SetMaxSize(value.Width, value.Height); }
    }

    /// <summary>
    /// Asks for client- or server-side decorations. The compositor decides; read <see cref="Decoration"/>
    /// after the next configure. Does nothing without xdg-decoration (decorations are then client-side).
    /// </summary>
    public void RequestDecoration(DecorationMode mode) => decoration?.SetMode((ZxdgToplevelDecorationV1Mode)mode);

    public void SetMaximized(bool maximized)
    {
        if (maximized)
            Role.SetMaximized();
        else
            Role.UnsetMaximized();
    }

    public void SetFullscreen(bool fullscreen, Output? output = null)
    {
        if (fullscreen)
            Role.SetFullscreen(output?.Proxy);
        else
            Role.UnsetFullscreen();
    }

    public void Minimize() => Role.SetMinimized();

    /// <summary>Starts an interactive move, typically on a title bar button press.</summary>
    public void Move(Seat seat) => Role.Move(seat.Proxy, seat.LastSerial);

    /// <summary>Starts an interactive resize from <paramref name="edge"/>, typically on a border button press.</summary>
    public void Resize(Seat seat, XdgToplevelResizeEdge edge) => Role.Resize(seat.Proxy, seat.LastSerial, edge);

    /// <summary>Shows the compositor's window menu at a logical position.</summary>
    public void ShowWindowMenu(Seat seat, int x, int y) => Role.ShowWindowMenu(seat.Proxy, seat.LastSerial, x, y);

    static ToplevelStates ParseStates(ReadOnlySpan<byte> array)
    {
        var states = ToplevelStates.None;
        foreach (uint state in System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(array))
        {
            // xdg_toplevel.state values start at 1, in the same order as ToplevelStates.
            if (state is >= 1 and <= 9)
                states |= (ToplevelStates)(1 << (int)(state - 1));
        }
        return states;
    }

    private protected override void DestroyRole()
    {
        decoration?.Dispose();
        Role.Dispose();
        XdgSurface.Dispose();
    }
}

public sealed partial class OopsApp
{
    XdgWmBase? wmBase;

    /// <summary><c>xdg_wm_base</c>, bound on first use, answering pings.</summary>
    internal XdgWmBase WmBase
    {
        get
        {
            if (wmBase is null)
            {
                wmBase = Get<XdgWmBase>();
                wmBase.Ping += e => wmBase.Pong(e.Serial);
            }
            return wmBase;
        }
    }
}
