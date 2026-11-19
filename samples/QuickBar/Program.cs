// QuickBar: the status bar built step by step in docs/guide/quickstart.md.
//   quickbar           run the bar (from your compositor's startup config)
//   quickbar toggle    hide or show it (bind this to a key)
using System.Collections.Specialized;
using oopshell;
using oopshell.Services.Audio;
using oopshell.Skia;
using SkiaSharp;

if (args is ["toggle"])
    return IpcServer.Send("quickbar", "toggle").IsOk ? 0 : 1;

using var app = OopsApp.Connect();
var bars = new List<Bar>();
void RedrawAll() => bars.ForEach(b => b.Invalidate());

// One bar per monitor, created and removed as monitors come and go.
IDisposable? perOutput = null;
void Show() => perOutput = app.ForEachOutput(output => new Bar(app, output, bars));
Show();

// Redraw when anything the bar shows changes.
using var clock = app.SetClockTimer(TimeSpan.FromMinutes(1), RedrawAll);
app.WorkspacesChanged += RedrawAll;
if (app.Toplevels is { } windows)
{
    foreach (var window in windows)
        window.Changed += RedrawAll;
    ((INotifyCollectionChanged)windows).CollectionChanged += (_, e) =>
    {
        foreach (ForeignToplevel window in e.NewItems ?? Array.Empty<ForeignToplevel>())
            window.Changed += RedrawAll;
        RedrawAll();
    };
}

// Volume is optional: without PulseAudio or PipeWire the bar just leaves it out.
app.Post(async () =>
{
    try
    {
        Bar.Audio = await AudioService.StartAsync(app);
        Bar.Audio.Changed += RedrawAll;
    }
    catch (InvalidOperationException)
    {
    }
});

// `quickbar toggle` from a key binding hides or shows every bar.
using var ipc = IpcServer.Listen(app, "quickbar", request =>
{
    if (request[0] != "toggle")
        throw new ArgumentException($"Unknown command '{request[0]}'.");
    if (perOutput is null)
        Show();
    else
    {
        perOutput.Dispose();
        perOutput = null;
    }
    return null;
});

return app.Run();

/// <summary>The bar on one monitor: workspaces on the left, the focused window in the middle, volume and clock on the right.</summary>
internal sealed class Bar : IDisposable
{
    private const int Height = 30;
    private static readonly SKColor s_background = new(0x1E, 0x22, 0x2A), s_text = new(0xD8, 0xDE, 0xE9), s_accent = new(0x81, 0xA1, 0xC1);
    private static readonly SKFont s_font = new(SKTypeface.Default, 14);
    private static readonly SKPaint s_paint = new() { IsAntialias = true };

    public static AudioService? Audio { get; set; }

    private readonly OopsApp _app;
    private readonly LayerSurface _surface;
    private readonly SkiaSurface _skia;
    private readonly List<Bar> _all;
    private readonly List<(SKRect Area, Workspace Workspace)> _workspaceButtons = new();
    private SKRect _volumeArea;

    public Bar(OopsApp app, Output output, List<Bar> all)
    {
        _app = app;
        _all = all;
        _surface = new LayerSurface(app, output, Layer.Top, "quickbar")
        {
            Anchor = Anchor.Top | Anchor.Left | Anchor.Right,
            Size = new(0, Height),   // 0: stretch between the left and right anchors
            ExclusiveZone = Height,  // windows are placed below the bar
        };
        _skia = new SkiaSurface(_surface);
        _surface.Render += Render;
        _surface.PointerButton += e =>
        {
            if (e.Pressed && e.Button == PointerButton.Left && _workspaceButtons.FirstOrDefault(b => b.Area.Contains((float)e.X, (float)e.Y)) is { Workspace: { } w })
                w.Activate();
        };
        _surface.PointerScroll += e =>
        {
            if (Audio?.DefaultSink is { } sink && _volumeArea.Contains((float)e.X, (float)e.Y))
                Audio.SetVolume(sink, Math.Clamp(sink.Volume - Math.Sign(e.DeltaY) * 0.05, 0, 1));
        };
        all.Add(this);
    }

    public void Invalidate() => _surface.Invalidate();

    private void Render(RenderFrame frame)
    {
        if (_skia.BeginFrame(frame) is not { } canvas)
            return;
        canvas.Scale((float)frame.Scale); // draw in logical pixels; Skia scales to the monitor
        float width = frame.LogicalSize.Width;
        canvas.Clear(s_background);
        float baseline = Height / 2f + 5;

        // Left: workspaces (compositors with ext-workspace, such as niri), the active one highlighted.
        _workspaceButtons.Clear();
        float x = 8;
        foreach (var workspace in _app.Workspaces is { } workspaces ? workspaces.ToList() : [])
        {
            string label = workspace.Name.Length > 0 ? workspace.Name : (_workspaceButtons.Count + 1).ToString();
            float w = s_font.MeasureText(label) + 16;
            var area = SKRect.Create(x, 4, w, Height - 8);
            s_paint.Color = workspace.IsActive ? s_accent : s_background.WithAlpha(0);
            canvas.DrawRoundRect(area, 4, 4, s_paint);
            Text(canvas, label, area.MidX, baseline, workspace.IsActive ? s_background : s_text, SKTextAlign.Center);
            _workspaceButtons.Add((area, workspace));
            x += w + 4;
        }

        // Middle: the focused window's title.
        if (_app.Toplevels?.FirstOrDefault(t => t.IsActivated) is { } focused)
            Text(canvas, focused.Title, width / 2, baseline, s_text, SKTextAlign.Center);

        // Right: volume (scroll over it to change) and the clock.
        string clock = DateTime.Now.ToString("ddd d MMM  HH:mm");
        Text(canvas, clock, width - 10, baseline, s_text, SKTextAlign.Right);
        _volumeArea = SKRect.Empty;
        if (Audio?.DefaultSink is { } sink)
        {
            string volume = sink.Muted ? "muted" : $"vol {Math.Round(sink.Volume * 100)}%";
            float right = width - 30 - s_font.MeasureText(clock);
            Text(canvas, volume, right, baseline, s_accent, SKTextAlign.Right);
            _volumeArea = SKRect.Create(right - s_font.MeasureText(volume), 0, s_font.MeasureText(volume), Height);
        }
        _skia.EndFrame();
    }

    private static void Text(SKCanvas canvas, string text, float x, float baseline, SKColor color, SKTextAlign align)
    {
        s_paint.Color = color;
        canvas.DrawText(text, x, baseline, align, s_font, s_paint);
    }

    public void Dispose()
    {
        _all.Remove(this);
        _skia.Dispose();
        _surface.Dispose();
    }
}
