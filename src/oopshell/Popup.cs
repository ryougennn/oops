using System.Drawing;
using oopshell.Protocol;

namespace oopshell;

/// <summary>Where a popup goes, relative to its parent (maps to <c>xdg_positioner</c>).</summary>
public sealed record PopupOptions
{
    /// <summary>Popup size in logical pixels.</summary>
    public required Size Size { get; init; }

    /// <summary>The rectangle the popup is placed against, such as a button, in the parent's logical coordinates.</summary>
    public required Rectangle AnchorRect { get; init; }

    /// <summary>Which point of <see cref="AnchorRect"/> the popup attaches to.</summary>
    public XdgPositionerAnchor Anchor { get; init; } = XdgPositionerAnchor.BottomLeft;

    /// <summary>Which way the popup extends from the anchor point.</summary>
    public XdgPositionerGravity Gravity { get; init; } = XdgPositionerGravity.BottomRight;

    /// <summary>How the compositor may move the popup to keep it on screen.</summary>
    public XdgPositionerConstraintAdjustment ConstraintAdjustment { get; init; } =
        XdgPositionerConstraintAdjustment.SlideX | XdgPositionerConstraintAdjustment.SlideY | XdgPositionerConstraintAdjustment.FlipY;

    public Point Offset { get; init; }

    /// <summary>Re-evaluates placement when the parent moves. Needs xdg_positioner v3.</summary>
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
    private readonly Surface _parent;
    private Seat? _grabSeat;
    private uint _grabSerial;
    private uint _repositionToken;
    private Margins _shadowMargins;
    private bool _geometrySent;

    public Popup(Surface parent, PopupOptions options) : base(parent.App)
    {
        _parent = parent;
        Options = options;
        var wmBase = App.WmBase;
        XdgSurface = wmBase.GetXdgSurface(Proxy);
        XdgSurface.Configure += e =>
        {
            XdgSurface.AckConfigure(e.Serial);
            ApplyGeometry();
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

    public Surface Parent => _parent;

    public PopupOptions Options { get; private set; }

    /// <summary>Where the compositor placed the popup, relative to the parent's window geometry.</summary>
    public Rectangle Geometry { get; private set; }

    /// <summary>Surface space outside the popup's geometry, for a client-side shadow.</summary>
    /// <remarks>
    /// Positioning uses the geometry (<see cref="PopupOptions.Size"/>). <see cref="Surface.LogicalSize"/> and
    /// render frames include the margins, so draw the popup inset by them. Child popups anchor in geometry
    /// coordinates: surface coordinates minus the left and top margins.
    /// </remarks>
    public Margins ShadowMargins
    {
        get => _shadowMargins;
        set
        {
            _shadowMargins = value;
            if (IsConfigured)
                ApplyGeometry();
        }
    }

    private void ApplyGeometry()
    {
        SendWindowGeometry(XdgSurface, _shadowMargins, Geometry.Size, ref _geometrySent);
        ApplyConfigure(GrowBy(Geometry.Size, _shadowMargins));
    }

    /// <summary>Raised when the compositor dismisses the popup, for example on an outside click. Dispose it then.</summary>
    public event Action? Dismissed;

    /// <summary>Takes an explicit grab, so keyboard input goes to the popup and an outside click dismisses it.</summary>
    /// <remarks>
    /// Call right after construction, from the input handler that opens the popup: the grab uses the seat's
    /// latest serial as of this call. A later serial, such as that of the button release, can lose the keyboard grab.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The popup has already been configured.</exception>
    public void Grab(Seat seat)
    {
        if (IsConfigured)
            throw new InvalidOperationException("Grab must be called before the popup is shown.");
        _grabSeat = seat;
        _grabSerial = seat.LastSerial;
    }

    /// <summary>Moves or resizes the popup.</summary>
    /// <exception cref="NotSupportedException">The compositor's xdg_popup is older than v3; recreate the popup instead.</exception>
    public void Reposition(PopupOptions options)
    {
        if (Role.BoundVersion < 3)
            throw new NotSupportedException("The compositor's xdg_popup does not support reposition; recreate the popup.");
        Options = options;
        using var positioner = options.Create(App.WmBase);
        Role.Reposition(positioner, ++_repositionToken);
    }

    private protected override void BeforeInitialCommit()
    {
        if (_grabSeat is not null)
            Role.Grab(_grabSeat.Proxy, _grabSerial);
    }

    private protected override int InitialScaleHint => (int)Math.Ceiling(_parent.Scale);

    private protected override void DestroyRole()
    {
        _parent.ChildPopups.Remove(this);
        Role.Dispose();
        XdgSurface.Dispose();
    }
}
