using System.Diagnostics;
using System.Drawing;

namespace oopshell.IntegrationTests;

/// <summary>
/// Window and output capture against tests/fixtures/capturewl (a tinywl with ext-image-copy-capture; no
/// packaged compositor offers window capture yet). Skips when the fixture is not built
/// (tests/fixtures/capturewl/build.sh).
/// </summary>
public sealed class CaptureTests
{
    static readonly string Binary = Path.Combine(SwayFixture.RepoRoot, "tests/fixtures/capturewl/capturewl");

    /// <summary>A capturewl instance in its own runtime dir.</summary>
    sealed class Compositor : IDisposable
    {
        readonly Process process;
        readonly string runtimeDir = Directory.CreateTempSubdirectory("oopscap.").FullName;

        public Compositor(bool withWlrToplevels)
        {
            Assert.SkipUnless(File.Exists(Binary), "capturewl is not built (tests/fixtures/capturewl/build.sh).");
            var start = new ProcessStartInfo(Binary, withWlrToplevels ? ["-w"] : []) { RedirectStandardError = true, RedirectStandardOutput = true };
            start.Environment["XDG_RUNTIME_DIR"] = runtimeDir;
            start.Environment["WLR_RENDERER"] = "pixman";
            start.Environment.Remove("WAYLAND_DISPLAY");
            process = Process.Start(start)!;
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && !File.Exists(Path.Combine(runtimeDir, "wayland-0")))
                Thread.Sleep(20);
        }

        public OopsApp Connect() => OopsApp.Connect(new OopsOptions { Display = Path.Combine(runtimeDir, "wayland-0") });

        public void Dispose()
        {
            process.Kill();
            process.WaitForExit();
            Directory.Delete(runtimeDir, recursive: true);
        }
    }

    static (byte B, byte G, byte R) Pixel(CapturedImage image, int x, int y)
    {
        int i = y * image.Stride + x * 4;
        return (image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2]);
    }

    static Toplevel Window(OopsApp app, string title, uint argb, out Func<int> frames)
    {
        var window = new Toplevel(app) { Title = title, AppId = "oopshell.capture-test", DefaultSize = new Size(320, 200) };
        frames = Draw.SolidColor(window, argb);
        return window;
    }

    [Fact]
    public void A_window_is_captured_with_its_own_pixels()
    {
        using var compositor = new Compositor(withWlrToplevels: false);
        using var app = compositor.Connect();
        Assert.True(ScreenCapture.CanCaptureWindows(app));
        using var window = Window(app, "blue window", 0xFF3060C0, out var frames);
        var toplevels = app.Toplevels!;
        app.RunUntil(() => frames() >= 1 && toplevels.Count == 1, what: "window listed");

        using var capture = ScreenCapture.ForWindow(app, toplevels[0]);
        Assert.NotNull(capture);
        var image = app.Complete(capture.CaptureAsync());
        Assert.Equal((320, 200), (image.Width, image.Height));
        Assert.Equal((0xC0, 0x60, 0x30), Pixel(image, 160, 100));
        Assert.Equal((0xC0, 0x60, 0x30), Pixel(image, 0, 0));

        // The session is reused; a second capture sees new contents.
        Draw.SolidColor(window, 0xFF20A040);
        window.Invalidate();
        app.RunUntil(() => frames() >= 2, what: "redraw");
        app.RunFor(100);
        Assert.Equal((0x40, 0xA0, 0x20), Pixel(app.Complete(capture.CaptureAsync()), 160, 100));
    }

    [Fact]
    public void Windows_are_paired_with_their_capture_handles_when_both_toplevel_lists_exist()
    {
        using var compositor = new Compositor(withWlrToplevels: true);
        using var app = compositor.Connect();
        using var red = Window(app, "red", 0xFFC02020, out var redFrames);
        using var green = Window(app, "green", 0xFF20C020, out var greenFrames);
        var toplevels = app.Toplevels!;
        app.RunUntil(() => redFrames() >= 1 && greenFrames() >= 1 && toplevels.Count == 2, what: "windows listed");
        Assert.All(toplevels, t => Assert.True(t.CanActivate)); // the wlr list, with actions

        foreach (var (title, expected) in new[] { ("red", (0x20, 0x20, 0xC0)), ("green", (0x20, 0xC0, 0x20)) })
        {
            var toplevel = toplevels.Single(t => t.Title == title);
            ScreenCapture? capture = null;
            app.RunUntil(() => (capture = ScreenCapture.ForWindow(app, toplevel)) is not null, what: $"{title} paired");
            using (capture)
                Assert.Equal(expected, Pixel(app.Complete(capture!.CaptureAsync()), 10, 10));
        }
    }

    [Fact]
    public void Outputs_are_captured_and_closed_windows_stop_their_capture()
    {
        using var compositor = new Compositor(withWlrToplevels: false);
        using var app = compositor.Connect();
        var window = Window(app, "short-lived", 0xFF8040C0, out var frames);
        var toplevels = app.Toplevels!;
        app.RunUntil(() => frames() >= 1 && toplevels.Count == 1, what: "window");

        using (var screen = ScreenCapture.ForOutput(app, app.Outputs[0]))
        {
            var image = app.Complete(screen!.CaptureAsync());
            Assert.Equal((800, 600), (image.Width, image.Height));
            Assert.Equal((0xC0, 0x40, 0x80), Pixel(image, 20, 60)); // capturewl puts the first window at (0, 40)
        }

        using var capture = ScreenCapture.ForWindow(app, toplevels[0])!;
        app.Complete(capture.CaptureAsync());
        bool stopped = false;
        capture.Stopped += () => stopped = true;
        window.Dispose();
        app.RunUntil(() => stopped, what: "capture stopped");
        var failure = app.Complete(capture.CaptureAsync().ContinueWith(t => t.Exception?.InnerException));
        Assert.IsType<InvalidOperationException>(failure);
    }
}
