using System.Drawing;
using oopshell.Protocol;

namespace oopshell;

/// <summary>A lock screen surface on one output (<c>ext_session_lock_surface_v1</c>). Created by <see cref="SessionLock"/>.</summary>
public sealed class LockSurface : Surface
{
    readonly ExtSessionLockSurfaceV1 role;

    internal LockSurface(OopsApp app, ExtSessionLockV1 sessionLock, Output output) : base(app)
    {
        Output = output;
        role = sessionLock.GetLockSurface(Proxy, output.Proxy);
        role.Configure += e =>
        {
            role.AckConfigure(e.Serial);
            ApplyConfigure(new Size((int)e.Width, (int)e.Height));
        };
    }

    public Output Output { get; }

    private protected override int InitialScaleHint => Output.Scale;

    // ext-session-lock configures right away; committing before the first ack is a protocol error.
    private protected override bool CommitsBeforeConfigure => false;

    internal void RaiseFinished() => RaiseClosed();

    private protected override void DestroyRole() => role.Dispose();
}

/// <summary>
/// A locked session (<c>ext-session-lock-v1</c>). Every output gets a <see cref="LockSurface"/>, including
/// outputs plugged in while locked. Call <see cref="Unlock"/> only after your code authenticated the user.
/// </summary>
/// <remarks>
/// If the process crashes or exits while locked, the compositor keeps the session locked (showing a
/// fallback or blank screen). That is intended: a crash must never unlock the session.
/// Recovery is compositor-specific (start a new locker, or switch VT and use the compositor's IPC).
/// </remarks>
public sealed class SessionLock
{
    readonly OopsApp app;
    readonly ExtSessionLockV1 proxy;
    readonly Action<LockSurface> configure;
    readonly Dictionary<Output, LockSurface> surfaces = new();
    readonly TaskCompletionSource<SessionLock> locked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    bool ended;

    internal SessionLock(OopsApp app, Action<LockSurface> configure)
    {
        this.app = app;
        this.configure = configure;
        proxy = app.Get<ExtSessionLockManagerV1>().Lock();
        proxy.Locked += () =>
        {
            IsLocked = true;
            locked.TrySetResult(this);
        };
        proxy.Finished += OnFinished;
        foreach (var output in app.Outputs)
            AddSurface(output);
        app.OutputAdded += AddSurface;
        app.OutputRemoved += RemoveSurface;
    }

    internal Task<SessionLock> LockedTask => locked.Task;

    /// <summary>True once the compositor confirmed the lock (every output shows a lock surface or is blanked).</summary>
    public bool IsLocked { get; private set; }

    public IReadOnlyCollection<LockSurface> Surfaces => surfaces.Values;

    /// <summary>The compositor ended the lock without an unlock (another locker took over, or it refused to lock).</summary>
    public event Action? Finished;

    /// <summary>Unlocks the session and destroys the lock surfaces.</summary>
    public void Unlock()
    {
        if (ended)
            return;
        if (!IsLocked)
            throw new InvalidOperationException("The session is not locked yet.");
        proxy.UnlockAndDestroy();
        End();
    }

    void AddSurface(Output output)
    {
        if (ended || surfaces.ContainsKey(output))
            return;
        var surface = new LockSurface(app, proxy, output);
        surfaces[output] = surface;
        configure(surface);
    }

    void RemoveSurface(Output output)
    {
        if (surfaces.Remove(output, out var surface))
            surface.Dispose();
    }

    void OnFinished()
    {
        if (ended)
            return;
        foreach (var surface in surfaces.Values)
            surface.RaiseFinished();
        proxy.Dispose();
        End();
        locked.TrySetException(new InvalidOperationException("The compositor refused to lock the session (is another locker running?)."));
        Finished?.Invoke();
    }

    void End()
    {
        ended = true;
        IsLocked = false;
        app.OutputAdded -= AddSurface;
        app.OutputRemoved -= RemoveSurface;
        foreach (var surface in surfaces.Values)
            surface.Dispose();
        surfaces.Clear();
    }
}

public sealed partial class OopsApp
{
    /// <summary>
    /// Locks the session. <paramref name="configure"/> is called for each output's lock surface (now and for
    /// outputs plugged in later): hook up its <see cref="Surface.Render"/> and input there. Completes once the
    /// compositor confirms the lock; throws if it refuses.
    /// </summary>
    public Task<SessionLock> LockAsync(Action<LockSurface> configure) => new SessionLock(this, configure).LockedTask;
}
