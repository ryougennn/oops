using System.Drawing;
using oopshell.Protocol;

namespace oopshell;

/// <summary>Where a popup goes, relative to its parent (maps to <c>xdg_positioner</c>).</summary>
public sealed record PopupOptions
{
    /// <summary>Popup size in logical pixels.</summary>
    public required Size Size { get; init; }

    /// <summary>The rectangle (in the parent's logical coordinates) the popup is placed against, e.g. a button.</summary>
    public required Rectangle AnchorRect { get; init; }

    /// <summary>Which point of <see cref="AnchorRect"/> the popup attaches to.</summary>
    public XdgPositionerAnchor Anchor { get; init; } = XdgPositionerAnchor.BottomLeft;

    /// <summary>Which way the popup extends from the anchor point.</summary>
    public XdgPositionerGravity Gravity { get; init; } = XdgPositionerGravity.BottomRight;

    /// <summary>How the compositor may move the popup to keep it on screen.</summary>
    public XdgPositionerConstraintAdjustment ConstraintAdjustment { get; init; } =
        XdgPositionerConstraintAdjustment.SlideX | XdgPositionerConstraintAdjustment.SlideY | XdgPositionerConstraintAdjustment.FlipY;

    public Point Offset { get; init; }

    /// <summary>Re-evaluate placement when the parent moves (xdg_positioner v3).</summary>
    public bool Reactive { get; init; }

    internal XdgPositioner Create(XdgWmBase wmBase)
    {
        var positioner = wmBase.CreatePositioner();
        positioner.SetSize(Size.Width, Size.Height);
        positioner.SetAnchorRect(AnchorRect.X, AnchorRect.Y, Math.Max(AnchorRect.Width, 1), Math.Max(AnchorRect.Height, 1));
        positioner.SetAnchor(Anchor);
        positioner.SetGravity(Gravity);
        positioner.SetConstraintAdjustment(ConstraintAdjustment);
        positioner.SetOffset(Offset.X, Offset.Y);
        if (Reactive && positioner.BoundVersion >= 3)
            positioner.SetReactive();
        return positioner;
    }
}

/// <summary>
/// A popup (<c>xdg_popup</c>) from a <see cref="Toplevel"/>, a <see cref="LayerSurface"/>, or another popup.
/// Call <see cref="Grab"/> right after creating it for menus that close on outside clicks.
/// </summary>
public sealed class Popup : Surface
{
    readonly Surface parent;
    Seat? grabSeat;
    uint repositionToken;

    public Popup(Surface parent, PopupOptions options) : base(parent.App)
    {
        this.parent = parent;
        Options = options;
        var wmBase = App.WmBase;
        XdgSurface = wmBase.GetXdgSurface(Proxy);
        XdgSurface.Configure += e =>
        {
            XdgSurface.AckConfigure(e.Serial);
            ApplyConfigure(Geometry.Size);
        };

        using (var positioner = options.Create(wmBase))
        {
            switch (parent)
            {
                case Toplevel toplevel:
                    Role = XdgSurface.GetPopup(toplevel.XdgSurface, positioner);
                    break;
                case Popup popup:
                    Role = XdgSurface.GetPopup(popup.XdgSurface, positioner);
                    break;
                case LayerSurface layer:
                    Role = XdgSurface.GetPopup(null, positioner);
                    layer.Role.GetPopup(Role);
                    break;
                default:
                    throw new ArgumentException($"A popup's parent must be a Toplevel, LayerSurface or Popup, not {parent.GetType().Name}.", nameof(parent));
            }
        }
        Role.Configure += e => Geometry = new Rectangle(e.X, e.Y, e.Width, e.Height);
        Role.PopupDone += () =>
        {
            Dismissed?.Invoke();
            RaiseClosed();
        };
        parent.ChildPopups.Add(this);
    }

    public XdgSurface XdgSurface { get; }

    /// <summary>The raw <c>xdg_popup</c> (escape hatch).</summary>
    public XdgPopup Role { get; }

    public Surface Parent => parent;

    public PopupOptions Options { get; private set; }

    /// <summary>Where the compositor placed the popup, relative to the parent's window geometry.</summary>
    public Rectangle Geometry { get; private set; }

    /// <summary>The compositor dismissed the popup (outside click, parent closed…). Dispose it.</summary>
    public event Action? Dismissed;

    /// <summary>
    /// Takes an explicit grab, so keyboard input goes here and a click outside dismisses the popup. Must be
    /// called before the popup is first committed (right after construction); uses the seat's latest serial.
    /// </summary>
    public void Grab(Seat seat)
    {
        if (IsConfigured)
            throw new InvalidOperationException("Grab must be called before the popup is shown.");
        grabSeat = seat;
    }

    /// <summary>Moves or resizes the popup. Needs xdg_popup v3; otherwise recreate the popup instead.</summary>
    public void Reposition(PopupOptions options)
    {
        if (Role.BoundVersion < 3)
            throw new NotSupportedException("The compositor's xdg_popup does not support reposition; recreate the popup.");
        Options = options;
        using var positioner = options.Create(App.WmBase);
        Role.Reposition(positioner, ++repositionToken);
    }

    private protected override void BeforeInitialCommit()
    {
        if (grabSeat is not null)
            Role.Grab(grabSeat.Proxy, grabSeat.LastSerial);
    }

    private protected override int InitialScaleHint => (int)Math.Ceiling(parent.Scale);

    private protected override void DestroyRole()
    {
        parent.ChildPopups.Remove(this);
        Role.Dispose();
        XdgSurface.Dispose();
    }
}
