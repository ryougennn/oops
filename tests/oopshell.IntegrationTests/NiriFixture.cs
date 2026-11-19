using System.Diagnostics;

namespace oopshell.IntegrationTests;

/// <summary>
/// A private niri for protocols sway lacks (ext-workspace): niri's nested backend inside a private headless
/// sway (GLES renderer, which niri needs), with its own session bus. Skips when niri is not installed.
/// </summary>
public sealed class NiriFixture : IDisposable
{
    readonly List<Process> processes = new();
    readonly DBusDaemon? bus;

    public NiriFixture(string extraConfig = "")
    {
        if (!SwayFixtureHelpers.OnPath("niri") || !SwayFixtureHelpers.OnPath("sway"))
        {
            SkipReason = "niri and sway are required.";
            return;
        }
        RuntimeDir = Directory.CreateTempSubdirectory("oopsniri.").FullName;
        File.SetUnixFileMode(RuntimeDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(RuntimeDir, "sway.conf"), "output HEADLESS-1 resolution 1280x800\ndefault_border none\nxwayland disable\n");
        File.WriteAllText(Path.Combine(RuntimeDir, "niri.kdl"), "hotkey-overlay { skip-at-startup; }\nprefer-no-csd\n" + extraConfig);

        var sway = Start("sway", ["-c", Path.Combine(RuntimeDir, "sway.conf")], new()
        {
            ["WLR_BACKENDS"] = "headless", ["WLR_LIBINPUT_NO_DEVICES"] = "1", ["WLR_RENDERER"] = "gles2",
        });
        string? swayDisplay = WaitFor(() => Directory.GetFiles(RuntimeDir, "wayland-*").FirstOrDefault(f => !f.EndsWith(".lock")));
        if (swayDisplay is null)
        {
            SkipReason = "headless sway did not start.";
            return;
        }
        bus = new DBusDaemon();
        Start("niri", ["-c", Path.Combine(RuntimeDir, "niri.kdl")], new()
        {
            ["WAYLAND_DISPLAY"] = Path.GetFileName(swayDisplay), ["DBUS_SESSION_BUS_ADDRESS"] = bus.Address,
        });
        WaylandSocket = WaitFor(() => Directory.GetFiles(RuntimeDir, "wayland-*")
            .FirstOrDefault(f => !f.EndsWith(".lock") && f != swayDisplay && File.Exists(f + ".lock")));
        if (WaylandSocket is null)
            SkipReason = "niri did not start.";
        _ = sway;
    }

    public string? SkipReason { get; }
    public string RuntimeDir { get; } = "";
    public string? WaylandSocket { get; }

    Process Start(string program, string[] args, Dictionary<string, string> env)
    {
        var start = new ProcessStartInfo(program, args) { RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment.Clear();
        start.Environment["HOME"] = Environment.GetEnvironmentVariable("HOME");
        start.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH");
        start.Environment["XDG_RUNTIME_DIR"] = RuntimeDir;
        foreach (var (k, v) in env)
            start.Environment[k] = v;
        var process = Process.Start(start)!;
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        processes.Add(process);
        return process;
    }

    static string? WaitFor(Func<string?> find)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (find() is { } found)
                return found;
            Thread.Sleep(100);
        }
        return null;
    }

    public OopsApp Connect()
    {
        Assert.SkipWhen(SkipReason is not null, SkipReason ?? "");
        return OopsApp.Connect(new OopsOptions { Display = WaylandSocket });
    }

    public void Dispose()
    {
        // niri first (it is sway's client), then sway.
        foreach (var process in Enumerable.Reverse(processes))
        {
            if (!process.HasExited)
                process.Kill();
            process.WaitForExit();
            process.Dispose();
        }
        bus?.Dispose();
        if (RuntimeDir.Length > 0)
            Directory.Delete(RuntimeDir, recursive: true);
    }
}

static class SwayFixtureHelpers
{
    public static bool OnPath(string program) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(dir => File.Exists(Path.Combine(dir, program)));
}
