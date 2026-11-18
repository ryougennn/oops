// An xdg_toplevel with an animated SHM gradient. It animates only while the pointer is over the window
// (so an idle window does no work), resizes with the compositor, and closes on request.
using oopshell;

using var app = OopsApp.Connect();
using var window = new Toplevel(app) { Title = "HelloWindow", AppId = "oopshell.HelloWindow" };
using var chain = new ShmSwapchain(window);

bool animating = args.Contains("--animate");
double phase = 0;

window.CloseRequested += () => app.Quit();
window.PointerEnter += _ => { animating = true; window.Invalidate(); };
window.PointerLeave += _ => animating = args.Contains("--animate");
window.Render += frame =>
{
    using var buffer = chain.Acquire(frame);
    if (buffer is null)
        return;
    DrawGradient(buffer, phase);
    buffer.Present(frame.Damage);
    if (animating)
    {
        phase += 0.02;
        window.Invalidate();
    }
};

return app.Run();

static void DrawGradient(ShmBuffer buffer, double phase)
{
    var pixels = buffer.Pixels;
    int width = buffer.Size.Width, height = buffer.Size.Height;
    for (int y = 0; y < height; y++)
    {
        var row = pixels.Slice(y * width, width);
        uint g = (uint)(255 * y / Math.Max(height - 1, 1));
        for (int x = 0; x < width; x++)
        {
            uint r = (uint)(255 * (0.5 + 0.5 * Math.Sin(phase + x * 6.0 / width)));
            row[x] = 0xFF000000 | (r << 16) | (g << 8) | 0x80;
        }
    }
}
