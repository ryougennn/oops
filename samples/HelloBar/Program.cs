// A bar at the bottom of every output (including ones plugged in later) that reserves its space.
// Only the "button" on the left takes input; the rest of the bar is click-through.
using System.Drawing;
using oopshell;

const int Height = 32;
var button = new Rectangle(4, 4, 80, Height - 8);

using var app = OopsApp.Connect();
using var bars = app.ForEachOutput(output =>
{
    var bar = new LayerSurface(app, output, Layer.Top, "panel")
    {
        Anchor = Anchor.Bottom | Anchor.Left | Anchor.Right,
        Size = new Size(0, Height),
        ExclusiveZone = Height,
        InputRegion = new Region(button),
    };
    var chain = new ShmSwapchain(bar);
    bool hovered = false;

    bar.PointerEnter += _ => { hovered = true; bar.Invalidate(); };
    bar.PointerLeave += _ => { hovered = false; bar.Invalidate(); };
    bar.PointerButton += e =>
    {
        if (e.Pressed && e.Button == PointerButton.Left)
            Console.WriteLine($"Button clicked on {output.Name}");
    };
    bar.Render += frame =>
    {
        using var buffer = chain.Acquire(frame);
        if (buffer is null)
            return;
        buffer.Pixels.Fill(0xFF202830);
        FillRect(buffer, Scale(button, frame.Scale), hovered ? 0xFF4A90D9u : 0xFF3A6EA5u);
        buffer.Present(frame.Damage);
    };
    bar.Closed += bar.Dispose;
    return new CompositeDisposable(chain, bar);
});

return app.Run();

static Rectangle Scale(Rectangle r, double s) =>
    Rectangle.FromLTRB((int)(r.Left * s), (int)(r.Top * s), (int)Math.Ceiling(r.Right * s), (int)Math.Ceiling(r.Bottom * s));

static void FillRect(ShmBuffer buffer, Rectangle r, uint color)
{
    r.Intersect(new Rectangle(Point.Empty, buffer.Size));
    for (int y = r.Top; y < r.Bottom; y++)
        buffer.Pixels.Slice(y * buffer.Size.Width + r.Left, r.Width).Fill(color);
}

sealed class CompositeDisposable(params IDisposable[] items) : IDisposable
{
    public void Dispose()
    {
        foreach (var item in items.Reverse())
            item.Dispose();
    }
}
