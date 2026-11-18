using System.Diagnostics;

namespace oopshell.IntegrationTests;

/// <summary>A private session bus for one test (what <c>dbus-run-session</c> does, but with the address in hand).</summary>
public sealed class DBusDaemon : IDisposable
{
    readonly Process process;

    public DBusDaemon()
    {
        Assert.SkipUnless(File.Exists("/usr/bin/dbus-daemon"), "dbus-daemon is not installed.");
        process = Process.Start(new ProcessStartInfo("dbus-daemon", ["--session", "--nofork", "--print-address=1"])
        {
            RedirectStandardOutput = true,
        })!;
        Address = process.StandardOutput.ReadLine() ?? throw new InvalidOperationException("dbus-daemon printed no address.");
    }

    public string Address { get; }

    /// <summary>Starts a tool on this bus and returns it (stdout redirected).</summary>
    public Process Start(string tool, params string[] args)
    {
        var start = new ProcessStartInfo(tool, args) { RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["DBUS_SESSION_BUS_ADDRESS"] = Address;
        return Process.Start(start)!;
    }

    public void Dispose()
    {
        process.Kill();
        process.WaitForExit();
        process.Dispose();
    }
}
