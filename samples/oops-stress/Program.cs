// Stress and profiling harness: drives one part of oopshell hard and prints one line of measurements
// (CPU, allocations, GCs, RSS, fds). Run it inside a throwaway compositor via tools/stress.sh.
//
//   oops-stress frames <raw|shm|gl|vulkan> [surfaces=4] [seconds=10]   continuous animation
//   oops-stress input [seconds=10]                                       pointer-motion flood
//   oops-stress churn [rounds=300]                                       create/destroy surfaces + popups
//   oops-stress outputs [rounds=20]                                      output hotplug (needs SWAYSOCK)
//   oops-stress notify [count=2000]                                      notification flood (needs a session bus)
//   oops-stress windows [count=40] [seconds=20]                          static windows, one retitled every 50 ms (load for a shell)
//   oops-stress sweep <y> [seconds=10]                                   1000 Hz pointer sweep along row y (load for a shell)
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using oopshell;
using oopshell.Protocol;
using oopshell.Services.DBus;
using oopshell.Services.Notifications;
using oopshell.Skia;
using SkiaSharp;
using Tmds.DBus.Protocol;

string scenario = args.ElementAtOrDefault(0) ?? "frames";
int Arg(int index, int fallback) => int.TryParse(args.ElementAtOrDefault(index), out int v) ? v : fallback;

using var app = OopsApp.Connect();
var stats = new Stats();
return scenario switch
{
    "frames" => Frames(app, args.ElementAtOrDefault(1) ?? "shm", Arg(2, 4), Arg(3, 10), stats),
    "input" => Input(app, Arg(1, 10), stats),
    "churn" => Churn(app, Arg(1, 300), stats),
    "outputs" => Outputs(app, Arg(1, 20), stats),
    "notify" => Notify(app, Arg(1, 2000), stats),
    "windows" => Windows(app, Arg(1, 40), Arg(2, 20)),
    "sweep" => Sweep(app, Arg(1, 590), Arg(2, 10)),
    _ => throw new ArgumentException($"unknown scenario {scenario}"),
};

static int Frames(OopsApp app, string backend, int count, int seconds, Stats stats)
{
    var windows = new List<IDisposable>();
    var renderTimes = new List<double>();
    int frames = 0;
    bool measuring = false;
    for (int i = 0; i < count; i++)
    {
        var window = new Toplevel(app) { Title = $"stress {i}", AppId = "oops-stress" };
        windows.Add(window);
        int index = i;
        if (backend == "raw")
        {
            var chain = new ShmSwapchain(window);
            windows.Add(chain);
            window.Render += frame =>
            {
                long start = Stopwatch.GetTimestamp();
                if (chain.Acquire(frame) is not { } buffer)
                    return;
                buffer.Pixels.Fill(0xFF000000u | (uint)(frames * 3 + index * 50) & 0xFF);
                buffer.Present();
                window.Invalidate();
                Count(start);
            };
        }
        else
        {
            var skia = new SkiaSurface(window, backend switch
            {
                "shm" => SkiaBackend.Shm,
                "vulkan" => SkiaBackend.Vulkan,
                _ => SkiaBackend.Auto,
            });
            windows.Add(skia);
            window.Render += frame =>
            {
                long start = Stopwatch.GetTimestamp();
                if (skia.BeginFrame(frame) is not { } canvas)
                    return;
                canvas.Scale((float)frame.Scale);
                Scene.Draw(canvas, frame.LogicalSize, frames + index * 17);
                skia.EndFrame();
                window.Invalidate();
                Count(start);
            };
            if (i == 0)
                Console.Error.WriteLine($"backend: {(skia.IsVulkan ? "vulkan" : skia.IsGpu ? "gl" : "shm")}");
        }
    }

    void Count(long start)
    {
        if (!measuring)
            return;
        frames++;
        renderTimes.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }

    // Warm up (first frames compile shaders), then measure.
    app.SetTimer(TimeSpan.FromSeconds(2), null, () => { measuring = true; stats.Start(); });
    app.SetTimer(TimeSpan.FromSeconds(2 + seconds), null, () => app.Quit());
    int code = app.Run();
    renderTimes.Sort();
    stats.Report($"frames {backend} x{count}", frames, "frame",
        $"render p50 {Percentile(renderTimes, 0.5):0.000} ms p99 {Percentile(renderTimes, 0.99):0.000} ms, {frames / (double)seconds / count:0.0} fps/surface");
    foreach (var w in Enumerable.Reverse(windows))
        w.Dispose();
    return code;
}

static int Input(OopsApp app, int seconds, Stats stats)
{
    using var window = new Toplevel(app) { Title = "stress input", AppId = "oops-stress" };
    using var chain = new ShmSwapchain(window);
    window.Render += frame =>
    {
        if (chain.Acquire(frame) is { } buffer)
        {
            buffer.Pixels.Fill(0xFF203040);
            buffer.Present();
        }
    };
    int moves = 0, injected = 0;
    window.PointerMove += _ => moves++;

    // A second connection on its own thread injects motion as fast as the compositor takes it.
    var stop = new CancellationTokenSource();
    var injector = new Thread(() =>
    {
        using var source = OopsApp.Connect();
        source.Display.Roundtrip();
        var pointer = source.Get<ZwlrVirtualPointerManagerV1>().CreateVirtualPointer(source.DefaultSeat!.Proxy);
        uint t = 0;
        while (!stop.IsCancellationRequested)
        {
            for (int i = 0; i < 50; i++, t++)
            {
                pointer.MotionAbsolute(t, 100 + t % 400, 100 + t / 7 % 300, 800, 600);
                pointer.Frame();
            }
            injected += 50;
            source.Display.Roundtrip(); // backpressure: never outrun the compositor
        }
        pointer.Dispose();
    });

    app.SetTimer(TimeSpan.FromSeconds(1), null, injector.Start);
    app.SetTimer(TimeSpan.FromSeconds(2), null, () => { moves = 0; stats.Start(); });
    app.SetTimer(TimeSpan.FromSeconds(2 + seconds), null, () => app.Quit());
    int code = app.Run();
    stop.Cancel();
    injector.Join();
    stats.Report("input motion", moves, "event", $"{moves / seconds}/s received, {injected / (seconds + 1)}/s injected");
    return code;
}

static int Churn(OopsApp app, int rounds, Stats stats)
{
    int round = 0, rendered = 0;
    void Step()
    {
        if (round == rounds)
        {
            app.Quit();
            return;
        }
        if (round == 20) // after warm-up (first Skia/font use)
            stats.Start();
        round++;
        var window = new Toplevel(app) { Title = $"churn {round}", AppId = "oops-stress" };
        var skia = new SkiaSurface(window, SkiaBackend.Shm);
        var layer = new LayerSurface(app, null, Layer.Overlay, "oops-stress") { Size = new Size(200, 30), Anchor = Anchor.Top };
        var layerChain = new ShmSwapchain(layer);
        var popup = new Popup(layer, new PopupOptions { Size = new Size(120, 200), AnchorRect = new Rectangle(0, 0, 200, 30) });
        var popupSkia = new SkiaSurface(popup, SkiaBackend.Shm);
        bool finished = false;
        layer.Render += frame =>
        {
            if (layerChain.Acquire(frame) is { } b) { b.Pixels.Fill(0xFF808080); b.Present(); }
        };
        popup.Render += frame =>
        {
            if (popupSkia.BeginFrame(frame) is { } c) { Scene.Draw(c, frame.LogicalSize, round); popupSkia.EndFrame(); }
            Finish();
        };
        window.Render += frame =>
        {
            if (skia.BeginFrame(frame) is { } canvas) { Scene.Draw(canvas, frame.LogicalSize, round); skia.EndFrame(); }
        };

        void Finish()
        {
            if (finished)
                return;
            finished = true;
            rendered++;
            // Tear everything down once the popup (the last to configure) has drawn, then go again.
            app.Post(() =>
            {
                popupSkia.Dispose();
                popup.Dispose();
                layerChain.Dispose();
                layer.Dispose();
                skia.Dispose();
                window.Dispose();
                app.Display.Roundtrip();
                Step();
            });
        }
    }
    app.Post(Step);
    int code = app.Run();
    stats.Report("churn toplevel+layer+popup", rounds - 20, "round", $"{rendered} rendered");
    return code;
}

static int Outputs(OopsApp app, int rounds, Stats stats)
{
    var bars = new Dictionary<Output, LayerSurface>();
    using var forEach = app.ForEachOutput(output =>
    {
        var bar = new LayerSurface(app, output, Layer.Top, "oops-stress-bar") { Size = new Size(0, 24), Anchor = Anchor.Top | Anchor.Left | Anchor.Right, ExclusiveZone = 24 };
        var skia = new SkiaSurface(bar, SkiaBackend.Shm);
        bar.Render += frame =>
        {
            if (skia.BeginFrame(frame) is { } c) { Scene.Draw(c, frame.LogicalSize, 0); skia.EndFrame(); }
        };
        bars[output] = bar;
        return new Disposer(() => { bars.Remove(output); skia.Dispose(); bar.Dispose(); });
    });

    int round = 0;
    string? added = null;
    void Step()
    {
        if (round == 2)
            stats.Start();
        if (added is not null)
        {
            Swaymsg("output", added, "unplug");
            added = null;
        }
        else if (round++ < rounds)
        {
            var before = app.Outputs.Select(o => o.Name).ToHashSet();
            Swaymsg("create_output");
            app.Display.Roundtrip();
            app.Display.Roundtrip();
            added = app.Outputs.Select(o => o.Name).FirstOrDefault(n => !before.Contains(n));
            if (added is null)
                Console.Error.WriteLine("output did not appear");
        }
        else
        {
            app.Quit();
            return;
        }
        app.SetTimer(TimeSpan.FromMilliseconds(150), null, Step);
    }
    app.Post(Step);
    int code = app.Run();
    stats.Report("output hotplug", rounds - 2, "round", $"bars now {bars.Count}, outputs {app.Outputs.Count}");
    return code;

    static void Swaymsg(params string[] a)
    {
        using var p = Process.Start(new ProcessStartInfo("swaymsg", a) { RedirectStandardOutput = true })!;
        p.WaitForExit();
    }
}

static int Notify(OopsApp app, int count, Stats stats)
{
    int sent = 0, closed = 0;
    NotificationServer? server = null;
    async Task RunAsync()
    {
        var serverBus = await OopsDBus.ConnectAsync(app);
        server = await NotificationServer.StartAsync(app, serverBus);
        server.DefaultTimeout = TimeSpan.FromMilliseconds(200);
        ((INotifyCollectionChanged)server.Notifications).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Remove)
                closed++;
        };
        var client = await OopsDBus.ConnectAsync(app);
        stats.Start();
        for (int i = 0; i < count; i++)
        {
            int n = i;
            await client.CallAsync("org.freedesktop.Notifications", "/org/freedesktop/Notifications", "org.freedesktop.Notifications", "Notify",
                (m, _) => m.GetBodyReader().ReadUInt32(), (ref MessageWriter w) =>
                {
                    w.WriteString("oops-stress");
                    w.WriteUInt32(0);
                    w.WriteString("");
                    w.WriteString($"Summary {n}");
                    w.WriteString("A body that is a little longer than the summary.");
                    w.WriteArray(new[] { "default", "Open" });
                    w.WriteDictionaryEnd(w.WriteDictionaryStart());
                    w.WriteInt32(-1);
                }, "susssasa{sv}i");
            sent++;
        }
        while (closed < count)
            await Task.Delay(50);
        app.Quit();
    }
    app.Post(() => _ = RunAsync().ContinueWith(t => { if (t.Exception is { } e) { Console.Error.WriteLine(e); app.Quit(1); } }));
    int code = app.Run();
    stats.Report("notifications", sent, "notification", $"{closed} expired, {server?.Notifications.Count} live");
    server?.Dispose();
    return code;
}

static int Windows(OopsApp app, int count, int seconds)
{
    var windows = new List<Toplevel>();
    for (int i = 0; i < count; i++)
    {
        var window = new Toplevel(app) { Title = $"Window {i}", AppId = $"oops-stress-{i % 5}" };
        var chain = new ShmSwapchain(window);
        window.Render += frame =>
        {
            if (chain.Acquire(frame) is { } b) { b.Pixels.Fill(0xFF406080); b.Present(); }
        };
        windows.Add(window);
    }
    int tick = 0;
    using var retitle = app.SetTimer(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(50), () =>
    {
        tick++;
        windows[tick % count].Title = $"Window {tick % count} — {tick}";
    });
    app.SetTimer(TimeSpan.FromSeconds(seconds), null, () => app.Quit());
    return app.Run();
}

static int Sweep(OopsApp app, int y, int seconds)
{
    app.Display.Roundtrip();
    using var pointer = app.Get<ZwlrVirtualPointerManagerV1>().CreateVirtualPointer(app.DefaultSeat!.Proxy);
    var output = app.Outputs[0].LogicalSize;
    uint t = 0;
    using var timer = app.SetTimer(TimeSpan.Zero, TimeSpan.FromMilliseconds(1), () =>
    {
        t++;
        uint x = t % (uint)(output.Width * 2) is var p && p < output.Width ? p : (uint)output.Width * 2 - p;
        pointer.MotionAbsolute(t, x, (uint)y, (uint)output.Width, (uint)output.Height);
        pointer.Frame();
    });
    app.SetTimer(TimeSpan.FromSeconds(seconds), null, () => app.Quit());
    return app.Run();
}

static double Percentile(List<double> sorted, double p) => sorted.Count == 0 ? 0 : sorted[(int)Math.Min(sorted.Count - 1, p * sorted.Count)];

internal sealed class Disposer(Action action) : IDisposable
{
    public void Dispose() => action();
}

internal static class Scene
{
    private static readonly SKFont s_font = new(SKFontManager.Default.MatchCharacter('A'), 14);
    private static readonly SKPaint s_fill = new() { IsAntialias = true };
    private static readonly SKPaint s_text = new() { Color = SKColors.Black, IsAntialias = true };

    // STRESS_SCENE=clear: measure the pipeline without the drawing cost.
    private static readonly bool s_clearOnly = Environment.GetEnvironmentVariable("STRESS_SCENE") == "clear";

    /// <summary>A taskbar/menu-like frame: background, bevelled rows, text, one moving highlight.</summary>
    public static void Draw(SKCanvas canvas, Size size, int tick)
    {
        canvas.Clear(new SKColor(0xC0, 0xC0, 0xC0));
        if (s_clearOnly)
            return;
        for (int y = 4, row = 0; y + 22 < size.Height && row < 24; y += 24, row++)
        {
            bool hot = row == tick / 4 % 24;
            s_fill.Color = hot ? new SKColor(0, 0, 0x80) : SKColors.White;
            canvas.DrawRoundRect(4, y, size.Width - 8, 20, 2, 2, s_fill);
            s_text.Color = hot ? SKColors.White : SKColors.Black;
            canvas.DrawText($"Item {row} — frame {tick}", 10, y + 15, SKTextAlign.Left, s_font, s_text);
        }
    }
}

/// <summary>Process-wide counters between <see cref="Start"/> and <see cref="Report"/>.</summary>
internal sealed class Stats
{
    private long _allocated, _ticks;
    private TimeSpan _cpu;
    private int _gen0, _gen1, _gen2, _fds;
    private long _rss, _heap;

    public void Start()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        _heap = GC.GetTotalMemory(true);
        _rss = Rss();
        _fds = Directory.GetFiles("/proc/self/fd").Length;
        _allocated = GC.GetTotalAllocatedBytes(true);
        _gen0 = GC.CollectionCount(0);
        _gen1 = GC.CollectionCount(1);
        _gen2 = GC.CollectionCount(2);
        _cpu = Process.GetCurrentProcess().TotalProcessorTime;
        _ticks = Stopwatch.GetTimestamp();
    }

    public void Report(string name, int units, string unit, string extra)
    {
        double wall = Stopwatch.GetElapsedTime(_ticks).TotalSeconds;
        double cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - _cpu).TotalMilliseconds;
        long alloc = GC.GetTotalAllocatedBytes(true) - _allocated;
        int g0 = GC.CollectionCount(0) - _gen0, g1 = GC.CollectionCount(1) - _gen1, g2 = GC.CollectionCount(2) - _gen2;
        long rssNow = Rss();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long heapNow = GC.GetTotalMemory(true);
        int fdsNow = Directory.GetFiles("/proc/self/fd").Length;
        Console.WriteLine($"{name}: {units} {unit}s in {wall:0.0}s | cpu {cpuMs / wall / 10:0.0}% ({cpuMs / Math.Max(units, 1) * 1000:0} µs/{unit}) | " +
            $"alloc {alloc / Math.Max(units, 1)} B/{unit} | gc {g0}/{g1}/{g2} | rss {_rss / 1024}→{rssNow / 1024} KiB | " +
            $"heap {_heap / 1024}→{heapNow / 1024} KiB | fds {_fds}→{fdsNow} | {extra}");
    }

    private static long Rss() => long.Parse(File.ReadAllText("/proc/self/statm").Split(' ')[1]) * Environment.SystemPageSize;
}
