using oopshell.Protocol;

namespace oopshell;

/// <summary>User idle detection (<c>ext-idle-notify-v1</c>). Get it from <see cref="OopsApp.Idle"/>.</summary>
public sealed class IdleMonitor
{
    private readonly OopsApp _app;

    internal IdleMonitor(OopsApp app) => _app = app;

    /// <summary>True if the compositor supports idle notifications.</summary>
    public bool IsSupported => _app.Has<ExtIdleNotifierV1>();

    /// <summary>
    /// Calls <paramref name="onIdle"/> after <paramref name="timeout"/> without user input, and
    /// <paramref name="onResumed"/> when input resumes. Idle inhibitors (e.g. a playing video) are respected
    /// unless <paramref name="ignoreInhibitors"/> (needs ext-idle-notify v2). Dispose to stop watching.
    /// </summary>
    public IDisposable Watch(TimeSpan timeout, Action onIdle, Action? onResumed = null, Seat? seat = null, bool ignoreInhibitors = false)
    {
        var notifier = _app.Get<ExtIdleNotifierV1>();
        var wlSeat = (seat ?? _app.DefaultSeat ?? throw new InvalidOperationException("No seat to watch.")).Proxy;
        uint ms = (uint)Math.Clamp(timeout.TotalMilliseconds, 1, uint.MaxValue);
        var notification = ignoreInhibitors && notifier.BoundVersion >= 2
            ? notifier.GetInputIdleNotification(ms, wlSeat)
            : notifier.GetIdleNotification(ms, wlSeat);
        notification.Idled += onIdle;
        if (onResumed is not null)
            notification.Resumed += onResumed;
        return notification;
    }
}

public sealed partial class OopsApp
{
    private IdleMonitor? _idle;

    /// <summary>Idle detection.</summary>
    public IdleMonitor Idle => _idle ??= new IdleMonitor(this);
}
