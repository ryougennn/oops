// oops-lab: one page per manual check of the oopshell services and protocols (docs/MANUAL_TESTS.md).
//   oops-lab               the lab window
//   oops-lab --greeter [--session "niri-session"]
//                          a minimal greetd greeter (run by greetd, inside e.g. cage or sway)
using oopshell;
using oopshell.Skia;
using OopsLab;

OopsApp app;
try
{
    app = OopsApp.Connect();
}
catch (oopshell.Protocol.WaylandConnectionException e)
{
    Console.Error.WriteLine($"oops-lab: {e.Message}");
    return 1;
}
using var connection = app;
using var window = new Toplevel(app) { Title = "oops-lab", AppId = "oops-lab", DefaultSize = new(980, 720) };
using var skia = new SkiaSurface(window);
window.CloseRequested += () => app.Quit();

Action<SkiaSharp.SKCanvas, float, float> draw;
if (args.Contains("--greeter"))
{
    int at = Array.IndexOf(args, "--session");
    var greeter = new Greeter(app, window, at >= 0 && at + 1 < args.Length ? args[at + 1] : "niri-session");
    window.SetFullscreen(true);
    draw = greeter.Draw;
    app.Post(greeter.Start);
}
else
{
    var lab = new Lab(app, window);
    draw = lab.Draw;
    app.Post(async () => await lab.StartServicesAsync());
}

window.Render += frame =>
{
    if (skia.BeginFrame(frame) is not { } canvas)
        return;
    canvas.Scale((float)frame.Scale);
    draw(canvas, frame.LogicalSize.Width, frame.LogicalSize.Height);
    skia.EndFrame();
};
return app.Run();
