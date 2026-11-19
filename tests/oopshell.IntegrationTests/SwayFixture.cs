using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace oopshell.IntegrationTests;

/// <summary>
/// Starts a headless sway (pixman renderer, no input devices) in a private runtime dir for a test collection.
/// Install: <c>dnf install sway grim</c> / <c>apt install sway grim</c>. Without them the tests skip.
/// </summary>
public sealed class SwayFixture : IDisposable
{
    public static readonly string RepoRoot = FindRepoRoot();
    private readonly Process? _sway;

    public SwayFixture()
    {
        if (!OnPath("sway") || !OnPath("swaymsg") || !OnPath("grim"))
        {
            SkipReason = "sway, swaymsg and grim are required (dnf install sway grim / apt install sway grim).";
            return;
        }
        // Unix socket paths are limited to 108 bytes, so the runtime dir must be short.
        RuntimeDir = Directory.CreateTempSubdirectory("oopsit.").FullName;
        File.SetUnixFileMode(RuntimeDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var start = new ProcessStartInfo("sway", ["-c", Path.Combine(RepoRoot, "tests/fixtures/sway.conf")])
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        start.Environment.Clear();
        start.Environment["HOME"] = Environment.GetEnvironmentVariable("HOME");
        start.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH");
        start.Environment["XDG_RUNTIME_DIR"] = RuntimeDir;
        start.Environment["WLR_BACKENDS"] = "headless";
        start.Environment["WLR_LIBINPUT_NO_DEVICES"] = "1";
        start.Environment["WLR_RENDERER"] = "pixman";
        _sway = Process.Start(start)!;
        _sway.OutputDataReceived += (_, e) => Log.Add(e.Data ?? "");
        _sway.ErrorDataReceived += (_, e) => Log.Add(e.Data ?? "");
        _sway.BeginOutputReadLine();
        _sway.BeginErrorReadLine();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && SwaySock is null)
        {
            Thread.Sleep(50);
            SwaySock = Directory.GetFiles(RuntimeDir, "sway-ipc.*").FirstOrDefault();
        }
        WaylandSocket = Directory.GetFiles(RuntimeDir, "wayland-*").FirstOrDefault(f => !f.EndsWith(".lock"));
        if (SwaySock is null || WaylandSocket is null)
            SkipReason = "headless sway did not start:\n" + string.Join("\n", Log);
    }

    public List<string> Log { get; } = new();
    public string? SkipReason { get; }
    public string RuntimeDir { get; } = "";
    /// <summary>Absolute path of the Wayland socket.</summary>
    public string? WaylandSocket { get; }
    public string? SwaySock { get; }

    /// <summary>Skips the calling test when sway is not available.</summary>
    public void RequireSway() => Assert.SkipWhen(SkipReason is not null, SkipReason ?? "");

    public OopsApp Connect(OopsOptions? options = null)
    {
        RequireSway();
        return OopsApp.Connect((options ?? new OopsOptions()) with { Display = WaylandSocket });
    }

    public string Swaymsg(params string[] args) => Run("swaymsg", args);

    /// <summary>The layout rectangle of the window with <paramref name="appId"/>, from sway's tree.</summary>
    public System.Drawing.Rectangle WindowRect(string appId)
    {
        using var tree = System.Text.Json.JsonDocument.Parse(Swaymsg("-t", "get_tree"));
        var node = FindNode(tree.RootElement, appId) ?? throw new InvalidOperationException($"No window with app_id {appId}.");
        var rect = node.GetProperty("rect");
        return new System.Drawing.Rectangle(rect.GetProperty("x").GetInt32(), rect.GetProperty("y").GetInt32(),
            rect.GetProperty("width").GetInt32(), rect.GetProperty("height").GetInt32());
    }

    private static System.Text.Json.JsonElement? FindNode(System.Text.Json.JsonElement node, string appId)
    {
        if (node.TryGetProperty("app_id", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String && id.GetString() == appId)
            return node;
        foreach (var listName in new[] { "nodes", "floating_nodes" })
        {
            if (!node.TryGetProperty(listName, out var children))
                continue;
            foreach (var child in children.EnumerateArray())
                if (FindNode(child, appId) is { } found)
                    return found;
        }
        return null;
    }

    /// <summary>Screenshot of the whole layout (or one output) via grim.</summary>
    public Png Screenshot(string? output = null)
    {
        string file = Path.Combine(RuntimeDir, $"shot-{Guid.NewGuid():N}.png");
        Run("grim", output is null ? [file] : ["-o", output, file]);
        var png = Png.Decode(File.ReadAllBytes(file));
        File.Delete(file);
        return png;
    }

    /// <summary>Runs a tool with this sway's environment.</summary>
    public string Run(string tool, params string[] args)
    {
        var start = new ProcessStartInfo(tool, args) { RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["XDG_RUNTIME_DIR"] = RuntimeDir;
        start.Environment["WAYLAND_DISPLAY"] = WaylandSocket;
        start.Environment["SWAYSOCK"] = SwaySock;
        using var process = Process.Start(start)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{tool} {string.Join(' ', args)} failed: {stderr}{stdout}");
        return stdout;
    }

    public void Dispose()
    {
        if (_sway is { HasExited: false })
        {
            try { Swaymsg("exit"); } catch { /* already gone */ }
            if (!_sway.WaitForExit(3000))
                _sway.Kill();
        }
        if (RuntimeDir.Length > 0)
            Directory.Delete(RuntimeDir, recursive: true);
    }

    internal static bool OnPath(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(dir => File.Exists(Path.Combine(dir, tool)));

    private static string FindRepoRoot([CallerFilePath] string path = "")
    {
        var dir = Path.GetDirectoryName(path);
        while (dir is not null && !File.Exists(Path.Combine(dir, "SPEC.md")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Repository root not found.");
    }
}

[CollectionDefinition("sway")]
public sealed class SwayCollection : ICollectionFixture<SwayFixture>;

public static class TestLoop
{
    /// <summary>Runs the app's loop until <paramref name="condition"/> holds; fails after <paramref name="timeoutMs"/>.</summary>
    public static void RunUntil(this OopsApp app, Func<bool> condition, int timeoutMs = 5000, string? what = null)
    {
        if (condition())
            return;
        const int TimedOut = 99;
        using var poll = app.SetTimer(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(5), () =>
        {
            if (condition())
                app.Quit();
        });
        using var timeout = app.SetTimer(TimeSpan.FromMilliseconds(timeoutMs), null, () => app.Quit(TimedOut));
        int code = app.Run();
        if (code == TimedOut)
            throw new TimeoutException($"Timed out waiting for {what ?? "condition"}.");
        if (app.ConnectionError is { } error)
            throw new InvalidOperationException("Connection died.", error);
    }

    /// <summary>
    /// Runs the loop until <paramref name="task"/> finishes and returns its result, staying on the app's thread
    /// (awaiting in a test would resume elsewhere, and Wayland objects are thread-affine).
    /// </summary>
    public static T Complete<T>(this OopsApp app, Task<T> task, int timeoutMs = 5000)
    {
        app.RunUntil(() => task.IsCompleted, timeoutMs, "task");
        return task.GetAwaiter().GetResult();
    }

    /// <summary>Runs the loop for a fixed time (to let things settle or to prove nothing happens).</summary>
    public static void RunFor(this OopsApp app, int milliseconds)
    {
        using var stop = app.SetTimer(TimeSpan.FromMilliseconds(milliseconds), null, () => app.Quit());
        app.Run();
    }
}
