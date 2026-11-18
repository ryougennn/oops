using System.Diagnostics;
using oopshell.Services.Audio;

namespace oopshell.IntegrationTests;

/// <summary>
/// Runs against the user's sound server, but only touches a temporary null sink it creates (and removes):
/// real outputs and the default output are left alone.
/// </summary>
[Collection("sway")]
public class AudioTests(SwayFixture sway)
{
    // The sway fixture has its own runtime dir; the sound server lives in the real one.
    static readonly string Server = $"unix:{Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")}/pulse/native";

    static string Pactl(params string[] args)
    {
        var start = new ProcessStartInfo("pactl", args) { RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["PULSE_SERVER"] = Server;
        start.Environment["LANG"] = "C";
        using var p = Process.Start(start)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"pactl {string.Join(' ', args)} failed: {p.StandardError.ReadToEnd()}");
        return output.Trim();
    }

    [Fact]
    public void Volume_and_mute_are_read_set_and_followed()
    {
        Assert.SkipUnless(File.Exists("/usr/bin/pactl") && File.Exists(Server[5..]), "No pactl or sound server.");
        using var app = sway.Connect();
        string name = $"oopshell_test_{Environment.ProcessId}";
        string module = Pactl("load-module", "module-null-sink", $"sink_name={name}", "channels=2");
        try
        {
            using var audio = app.Complete(AudioService.StartAsync(app, Server));
            app.RunUntil(() => audio.Sinks.Any(s => s.Name == name), what: "test sink listed");
            var sink = audio.Sinks.First(s => s.Name == name);
            Assert.Equal(2, sink.Channels);
            Assert.False(sink.Muted);
            Assert.NotEqual(name, audio.DefaultSink?.Name); // the default output is not ours to change

            // Our changes, as the server reports them back.
            audio.SetVolume(sink, 0.25);
            app.RunUntil(() => Math.Abs(audio.Sinks.First(s => s.Name == name).Volume - 0.25) < 0.01, what: "volume set");
            Assert.Contains("25%", Pactl("get-sink-volume", name));
            audio.SetMuted(sink, true);
            app.RunUntil(() => audio.Sinks.First(s => s.Name == name).Muted, what: "muted");

            // Someone else's change.
            int changes = 0;
            audio.Changed += () => changes++;
            Pactl("set-sink-volume", name, "60%");
            app.RunUntil(() => Math.Abs(audio.Sinks.First(s => s.Name == name).Volume - 0.6) < 0.01, what: "external change");
            Assert.True(changes > 0);
        }
        finally
        {
            Pactl("unload-module", module);
        }
    }
}
