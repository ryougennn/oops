using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using oopshell.Native;
using static oopshell.Native.LibC;

namespace oopshell;

/// <summary>Readiness flags for <see cref="OopsApp.WatchFd"/>.</summary>
[Flags]
public enum FdEvents
{
    None = 0,
    Readable = 1,
    Writable = 4,
    Error = 8,
    HangUp = 16,
}

/// <summary>
/// Single-threaded epoll loop: posted work, timers (one timerfd each), watched fds, and one "source" fd
/// (the Wayland connection) that the owner drives through <see cref="IEventSource"/>.
/// </summary>
internal sealed unsafe class EventLoop : IDisposable
{
    private readonly int _epoll;
    private readonly int _wakeFd;
    private readonly ConcurrentQueue<Action> _posted = new();
    private readonly Dictionary<int, Action<FdEvents>> _watches = new();
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private readonly HashSet<int> _removedThisBatch = new();
    private bool _running;

    /// <summary>Called with an exception a callback threw; true means it was dealt with and the loop goes on.</summary>
    public Func<Exception, bool>? ErrorHandler { get; set; }

    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (ErrorHandler?.Invoke(e) == true)
        {
        }
    }

    public EventLoop()
    {
        _epoll = epoll_create1(EPOLL_CLOEXEC);
        if (_epoll < 0)
            throw LastError("epoll_create1");
        _wakeFd = eventfd(0, EFD_CLOEXEC | EFD_NONBLOCK);
        if (_wakeFd < 0)
            throw LastError("eventfd");
        Add(_wakeFd, EPOLLIN);
    }

    public bool IsLoopThread => Environment.CurrentManagedThreadId == _ownerThread;

    /// <summary>Queues work for the loop thread. Safe from any thread.</summary>
    public void Post(Action action)
    {
        _posted.Enqueue(action);
        ulong one = 1;
        write(_wakeFd, &one, sizeof(ulong));
    }

    public IDisposable WatchFd(int fd, FdEvents events, Action<FdEvents> callback)
    {
        if (_watches.ContainsKey(fd))
            throw new InvalidOperationException($"fd {fd} is already watched.");
        _watches[fd] = callback;
        Add(fd, (uint)events);
        return new Disposer(() =>
        {
            if (_watches.Remove(fd))
            {
                EpollCtl(_epoll, EPOLL_CTL_DEL, fd, 0, 0);
                _removedThisBatch.Add(fd); // the number may be reused before this batch's events are delivered
            }
        });
    }

    /// <summary>Changes the events an already watched fd is interested in.</summary>
    public void ModifyWatch(int fd, FdEvents events) => EpollCtl(_epoll, EPOLL_CTL_MOD, fd, (uint)events, (ulong)fd);

    public IDisposable SetTimer(TimeSpan due, TimeSpan? period, Action callback)
    {
        int fd = timerfd_create(CLOCK_MONOTONIC, TFD_CLOEXEC | TFD_NONBLOCK);
        if (fd < 0)
            throw LastError("timerfd_create");
        var spec = new ITimerSpec
        {
            // A zero "value" disarms a timerfd, so a due time of zero becomes one nanosecond.
            Value = due > TimeSpan.Zero ? TimeSpec.From(due) : new TimeSpec { Nanoseconds = 1 },
            Interval = period is { } p ? TimeSpec.From(p) : default,
        };
        if (timerfd_settime(fd, 0, &spec, null) < 0)
        {
            close(fd);
            throw LastError("timerfd_settime");
        }

        IDisposable? watch = null;
        bool disposed = false;
        void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            watch?.Dispose();
            close(fd);
        }
        watch = WatchFd(fd, FdEvents.Readable, _ =>
        {
            ulong expirations;
            if (read(fd, &expirations, sizeof(ulong)) < 0)
                return;
            if (period is null)
                Dispose();
            callback();
        });
        return new Disposer(Dispose);
    }

    /// <summary>
    /// Calls <paramref name="callback"/> at every multiple of <paramref name="interval"/> of local wall-clock
    /// time (every minute on the minute…), from an absolute CLOCK_REALTIME deadline re-armed each time: one
    /// that passed during suspend fires on resume, and setting the clock fires it at once and realigns it.
    /// </summary>
    public IDisposable SetClockTimer(TimeSpan interval, Action callback)
    {
        if (interval < TimeSpan.FromMilliseconds(1))
            throw new ArgumentOutOfRangeException(nameof(interval), "The interval must be at least a millisecond.");
        int fd = timerfd_create(CLOCK_REALTIME, TFD_CLOEXEC | TFD_NONBLOCK);
        if (fd < 0)
            throw LastError("timerfd_create");
        void Arm()
        {
            // The next boundary in local time (so hours and days follow the time zone), as an absolute UTC deadline.
            long local = DateTime.Now.Ticks;
            var next = new DateTime((local / interval.Ticks + 1) * interval.Ticks, DateTimeKind.Local).ToUniversalTime();
            var spec = new ITimerSpec { Value = TimeSpec.From(next - DateTime.UnixEpoch) };
            if (timerfd_settime(fd, TFD_TIMER_ABSTIME | TFD_TIMER_CANCEL_ON_SET, &spec, null) < 0)
                throw LastError("timerfd_settime");
        }
        try
        {
            Arm();
        }
        catch
        {
            close(fd);
            throw;
        }
        var watch = WatchFd(fd, FdEvents.Readable, _ =>
        {
            ulong expirations;
            // Either the deadline passed or the clock was set (ECANCELED): both mean realign and tell the caller.
            if (read(fd, &expirations, sizeof(ulong)) < 0 && Marshal.GetLastPInvokeError() is not ECANCELED)
                return;
            Arm();
            callback();
        });
        return new Disposer(() =>
        {
            watch.Dispose();
            close(fd);
        });
    }

    /// <summary>Runs until <see cref="Stop"/>. <paramref name="source"/> is called around each wait.</summary>
    public void Run(IEventSource source)
    {
        _running = true;
        Span<EpollEvent> events = stackalloc EpollEvent[32];
        while (_running)
        {
            RunPosted();
            if (!_running)
                break;
            Guard(source.BeforeWait);
            int timeout = _posted.IsEmpty ? -1 : 0;
            int count = EpollWait(_epoll, events, timeout);
            if (count < 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                source.AfterWait(FdEvents.None);
                if (errno == EINTR)
                    continue;
                throw new IOException($"epoll_wait failed: {ErrorText(errno)}");
            }

            var sourceEvents = FdEvents.None;
            for (int i = 0; i < count; i++)
            {
                if ((int)events[i].Data == source.Fd)
                    sourceEvents = (FdEvents)events[i].Events;
            }
            Guard(() => source.AfterWait(sourceEvents));

            _removedThisBatch.Clear();
            for (int i = 0; i < count; i++)
            {
                int fd = (int)events[i].Data;
                if (fd == _wakeFd)
                {
                    ulong value;
                    read(_wakeFd, &value, sizeof(ulong));
                }
                else if (fd != source.Fd && !_removedThisBatch.Contains(fd) && _watches.TryGetValue(fd, out var callback))
                {
                    var happened = (FdEvents)events[i].Events;
                    Guard(() => callback(happened));
                }
            }
        }
    }

    public void Stop()
    {
        _running = false;
        Post(() => { });
    }

    /// <summary>Registers the source fd. Its events reach <see cref="IEventSource.AfterWait"/>, not a watch callback.</summary>
    public void AddSource(int fd) => Add(fd, EPOLLIN);

    private void RunPosted()
    {
        // Only run what was queued before we started, so work that re-posts itself cannot starve the loop.
        int count = _posted.Count;
        for (int i = 0; i < count && _posted.TryDequeue(out var action); i++)
            Guard(action);
    }

    private void Add(int fd, uint events)
    {
        if (EpollCtl(_epoll, EPOLL_CTL_ADD, fd, events, (ulong)fd) < 0)
            throw LastError("epoll_ctl");
    }

    public void Dispose()
    {
        close(_wakeFd);
        close(_epoll);
    }
}

/// <summary>The loop's main fd and the hooks that run around each wait (for the Wayland read protocol).</summary>
internal interface IEventSource
{
    public int Fd { get; }
    public void BeforeWait();
    public void AfterWait(FdEvents events);
}

/// <summary>Runs an action once on dispose.</summary>
internal sealed class Disposer(Action action) : IDisposable
{
    private Action? _action = action;

    public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
}

/// <summary>Resumes <c>await</c> continuations on the loop thread.</summary>
internal sealed class LoopSynchronizationContext(EventLoop loop) : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state) => loop.Post(() => d(state));

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (loop.IsLoopThread)
        {
            d(state);
            return;
        }
        using var done = new ManualResetEventSlim();
        Exception? error = null;
        loop.Post(() =>
        {
            try { d(state); }
            catch (Exception e) { error = e; }
            finally { done.Set(); }
        });
        done.Wait();
        if (error is not null)
            throw new AggregateException(error);
    }

    public override SynchronizationContext CreateCopy() => this;
}
