using System.Drawing;
using oopshell.Protocol;

namespace oopshell;

/// <summary>A union of rectangles in logical surface coordinates. Immutable.</summary>
public sealed class Region(params Rectangle[] rectangles)
{
    /// <summary>A region containing nothing (e.g. an input region for a click-through surface).</summary>
    public static readonly Region Empty = new();

    /// <summary>Everything: the compositor clips it to the surface (e.g. a blur region for a whole surface).</summary>
    public static readonly Region All = new(new Rectangle(0, 0, int.MaxValue, int.MaxValue));

    public IReadOnlyList<Rectangle> Rectangles { get; } = rectangles.ToArray();

    internal WlRegion Create(WlCompositor compositor)
    {
        var region = compositor.CreateRegion();
        foreach (var r in Rectangles)
            region.Add(r.X, r.Y, r.Width, r.Height);
        return region;
    }
}

/// <summary>What a <see cref="Surface.Render"/> handler gets: sizes, scale, and what to redraw.</summary>
/// <param name="BufferSize">Size of the buffer to present, in pixels.</param>
/// <param name="LogicalSize">Size of the surface in logical pixels.</param>
/// <param name="Scale">Buffer pixels per logical pixel.</param>
/// <param name="Damage">Area that needs redrawing, in buffer pixels (the whole buffer after a resize or scale change).</param>
/// <param name="Time">Timestamp of the frame callback that triggered this frame (milliseconds, arbitrary base).</param>
public readonly record struct RenderFrame(Size BufferSize, Size LogicalSize, double Scale, Rectangle Damage, uint Time)
{
    /// <summary>The whole buffer must be redrawn (first frame, resize, scale change, or no usable history).</summary>
    public bool IsFullDamage => Damage == new Rectangle(Point.Empty, BufferSize);
}

/// <summary>Sizes after a configure (or a scale change).</summary>
public readonly record struct SurfaceConfiguredEventArgs(Size LogicalSize, double Scale, Size BufferSize);
