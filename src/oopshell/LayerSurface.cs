using System.Drawing;
using oopshell.Protocol;

namespace oopshell;

/// <summary>Layer of a <see cref="LayerSurface"/>, bottom to top.</summary>
public enum Layer
{
    Background = 0,
    Bottom = 1,
    Top = 2,
    Overlay = 3,
}

/// <summary>Edges a layer surface is anchored to.</summary>
[Flags]
public enum Anchor
{
    None = 0,
    Top = 1,
    Bottom = 2,
    Left = 4,
    Right = 8,
    All = Top | Bottom | Left | Right,
}

/// <summary>Whether a layer surface gets keyboard focus.</summary>
public enum KeyboardInteractivity
{
    /// <summary>Never gets keyboard focus (bars, docks, wallpapers).</summary>
    None = 0,
    /// <summary>Takes keyboard focus as long as it is mapped (top and overlay layers).</summary>
    Exclusive = 1,
    /// <summary>Gets focus like a normal window when clicked.</summary>
    OnDemand = 2,
}

/// <summary>Distances from the anchored edges, in logical pixels.</summary>
public readonly record struct Margins(int Top, int Right, int Bottom, int Left)
{
    public static Margins All(int value) => new(value, value, value, value);
}

/// <summary>
/// A shell surface (<c>zwlr_layer_surface_v1</c>): bars, docks, wallpapers, overlays.
/// Every property can be changed at any time; changes apply with the next commit.
/// </summary>
public sealed class LayerSurface : Surface
{
    Layer layer;
    Anchor anchor;
    Margins margins;
    Size size;
    int exclusiveZone;
    KeyboardInteractivity keyboardInteractivity;

    /// <param name="app">The app.</param>
    /// <param name="output">Output to show on, or null to let the compositor choose.</param>
    /// <param name="layer">Initial layer.</param>
    /// <param name="namespace">Purpose of the surface (e.g. "panel"); compositors use it for rules.</param>
    public LayerSurface(OopsApp app, Output? output = null, Layer layer = Layer.Top, string @namespace = "oopshell") : base(app)
    {
        Output = output;
        Namespace = @namespace;
        this.layer = layer;
        Role = app.Get<ZwlrLayerShellV1>().GetLayerSurface(Proxy, output?.Proxy, (ZwlrLayerShellV1Layer)layer, @namespace);
        Role.Configure += e =>
        {
            Role.AckConfigure(e.Serial);
            // 0 means "you choose", which only happens for dimensions we gave ourselves.
            ApplyConfigure(new Size(e.Width > 0 ? (int)e.Width : size.Width, e.Height > 0 ? (int)e.Height : size.Height));
        };
        Role.Closed += RaiseClosed;
    }

    /// <summary>The raw <c>zwlr_layer_surface_v1</c> (escape hatch).</summary>
    public ZwlrLayerSurfaceV1 Role { get; }

    /// <summary>The output this surface was created for (null = compositor's choice).</summary>
    public Output? Output { get; }

    public string Namespace { get; }

    private protected override int InitialScaleHint => Output?.Scale ?? 1;

    /// <summary>Changing the layer needs layer-shell v2.</summary>
    public Layer Layer
    {
        get => layer;
        set { layer = value; Role.SetLayer((ZwlrLayerShellV1Layer)value); Changed(); }
    }

    public Anchor Anchor
    {
        get => anchor;
        set { anchor = value; Role.SetAnchor((ZwlrLayerSurfaceV1Anchor)value); Changed(); }
    }

    public Margins Margins
    {
        get => margins;
        set { margins = value; Role.SetMargin(value.Top, value.Right, value.Bottom, value.Left); Changed(); }
    }

    /// <summary>Requested logical size. Use 0 for a dimension anchored on both sides to stretch it.</summary>
    public Size Size
    {
        get => size;
        set { size = value; Role.SetSize((uint)value.Width, (uint)value.Height); Changed(); }
    }

    /// <summary>Space reserved at the anchored edge (positive), -1 to ignore other zones, 0 for none.</summary>
    public int ExclusiveZone
    {
        get => exclusiveZone;
        set { exclusiveZone = value; Role.SetExclusiveZone(value); Changed(); }
    }

    public KeyboardInteractivity KeyboardInteractivity
    {
        get => keyboardInteractivity;
        set
        {
            keyboardInteractivity = value;
            Role.SetKeyboardInteractivity((ZwlrLayerSurfaceV1KeyboardInteractivity)value);
            Changed();
        }
    }

    // Changes take effect on commit. Outside a render they are committed at once, without a new buffer:
    // waiting for the next frame (which may wait on a frame callback) could let later requests overtake them,
    // e.g. a popup's grab reaching the compositor before the keyboard interactivity it depends on.
    void Changed()
    {
        if (!IsConfigured || IsRendering)
            return; // before the first configure, or the frame being drawn commits it
        if (HasPresented)
            Proxy.Commit();
        else
            Invalidate(Rectangle.Empty);
    }

    private protected override void DestroyRole() => Role.Dispose();
}
