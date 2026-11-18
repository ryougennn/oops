using System.Diagnostics;
using oopshell.Protocol;

namespace oopshell;

/// <summary>Options for <see cref="OopsApp.LaunchAsync"/>.</summary>
public sealed record LaunchOptions
{
    /// <summary>The surface the user interacted with to launch (e.g. the clicked button's surface). Lets the compositor hand focus over.</summary>
    public Surface? Surface { get; init; }

    /// <summary>The seat whose latest input event triggered the launch; defaults to the app's first seat.</summary>
    public Seat? Seat { get; init; }

    /// <summary>The launched app's id, a hint for the compositor.</summary>
    public string? AppId { get; init; }

    public string? WorkingDirectory { get; init; }

    /// <summary>Extra environment variables for the child.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}

public sealed partial class OopsApp
{
    /// <summary>
    /// Starts <paramref name="program"/> detached (it outlives the shell, via <c>setsid --fork</c>) with an
    /// xdg-activation token in <c>XDG_ACTIVATION_TOKEN</c> and <c>DESKTOP_STARTUP_ID</c>, so the new window
    /// may take focus. Without xdg-activation the program starts without a token.
    /// </summary>
    public async Task LaunchAsync(string program, IEnumerable<string>? args = null, LaunchOptions? options = null)
    {
        options ??= new LaunchOptions();
        string? token = await RequestActivationTokenAsync(options);

        bool detach = File.Exists("/usr/bin/setsid");
        var start = new ProcessStartInfo(detach ? "/usr/bin/setsid" : program) { UseShellExecute = false };
        if (detach)
        {
            start.ArgumentList.Add("--fork");
            start.ArgumentList.Add(program);
        }
        foreach (var arg in args ?? [])
            start.ArgumentList.Add(arg);
        if (options.WorkingDirectory is not null)
            start.WorkingDirectory = options.WorkingDirectory;
        if (token is not null)
        {
            start.Environment["XDG_ACTIVATION_TOKEN"] = token;
            start.Environment["DESKTOP_STARTUP_ID"] = token;
        }
        foreach (var (key, value) in options.Environment ?? new Dictionary<string, string>())
            start.Environment[key] = value;

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {program}.");
        if (detach)
            await process.WaitForExitAsync(); // setsid --fork exits as soon as the child is forked
    }

    /// <summary>Asks the compositor for an xdg-activation token, or returns null if it lacks xdg-activation.</summary>
    public Task<string?> RequestActivationTokenAsync(LaunchOptions? options = null)
    {
        if (TryGet<XdgActivationV1>() is not { } activation)
            return Task.FromResult<string?>(null);
        options ??= new LaunchOptions();
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = activation.GetActivationToken();
        var seat = options.Seat ?? DefaultSeat;
        if (seat is not null && seat.LastSerial != 0)
            request.SetSerial(seat.LastSerial, seat.Proxy);
        if (options.Surface is not null)
            request.SetSurface(options.Surface.Proxy);
        if (options.AppId is not null)
            request.SetAppId(options.AppId);
        request.Done += e =>
        {
            done.TrySetResult(e.Token);
            request.Dispose();
        };
        request.Commit();
        return done.Task;
    }

    /// <summary>Asks the compositor to focus <paramref name="surface"/> using a token (e.g. one passed to this process at startup).</summary>
    public void Activate(Surface surface, string token) => Get<XdgActivationV1>().Activate(token, surface.Proxy);
}
