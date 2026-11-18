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
    readonly int epoll;
    readonly int wakeFd;
    readonly ConcurrentQueue<Action> posted = new();
    readonly Dictionary<int, Action<FdEvents>> watches = new();
    readonly int ownerThread = Environment.CurrentManagedThreadId;
    bool running;

    public EventLoop()
    {
        epoll = epoll_create1(EPOLL_CLOEXEC);
        if (epoll < 0)
            throw LastError("epoll_create1");
        wakeFd = eventfd(0, EFD_CLOEXEC | EFD_NONBLOCK);
        if (wakeFd < 0)
            throw LastError("eventfd");
        Add(wakeFd, EPOLLIN);
    }

    public bool IsLoopThread => Environment.CurrentManagedThreadId == ownerThread;

    /// <summary>Queues work for the loop thread. Safe from any thread.</summary>
    public void Post(Action action)
    {
        posted.Enqueue(action);
        ulong one = 1;
        write(wakeFd, &one, sizeof(ulong));
    }

    public IDisposable WatchFd(int fd, FdEvents events, Action<FdEvents> callback)
    {
        if (watches.ContainsKey(fd))
            throw new InvalidOperationException($"fd {fd} is already watched.");
        watches[fd] = callback;
        Add(fd, (uint)events);
        return new Disposer(() =>
        {
            if (watches.Remove(fd))
                EpollCtl(epoll, EPOLL_CTL_DEL, fd, 0, 0);
        });
    }

    /// <summary>Changes the events an already watched fd is interested in.</summary>
    public void ModifyWatch(int fd, FdEvents events) => EpollCtl(epoll, EPOLL_CTL_MOD, fd, (uint)events, (ulong)fd);

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

    /// <summary>Runs until <see cref="Stop"/>. <paramref name="source"/> is called around each wait.</summary>
    public void Run(IEventSource source)
    {
        running = true;
        Span<EpollEvent> events = stackalloc EpollEvent[32];
        while (running)
        {
            RunPosted();
            if (!running)
                break;
            source.BeforeWait();
            int timeout = posted.IsEmpty ? -1 : 0;
            int count = EpollWait(epoll, events, timeout);
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
            source.AfterWait(sourceEvents);

            for (int i = 0; i < count; i++)
            {
                int fd = (int)events[i].Data;
                if (fd == wakeFd)
                {
                    ulong value;
                    read(wakeFd, &value, sizeof(ulong));
                }
                else if (fd != source.Fd && watches.TryGetValue(fd, out var callback))
                {
                    callback((FdEvents)events[i].Events);
                }
            }
        }
    }

    public void Stop()
    {
        running = false;
        Post(() => { });
    }

    /// <summary>Registers the source fd. Its events reach <see cref="IEventSource.AfterWait"/>, not a watch callback.</summary>
    public void AddSource(int fd) => Add(fd, EPOLLIN);

    void RunPosted()
    {
        // Only run what was queued before we started, so work that re-posts itself cannot starve the loop.
        int count = posted.Count;
        for (int i = 0; i < count && posted.TryDequeue(out var action); i++)
            action();
    }

    void Add(int fd, uint events)
    {
        if (EpollCtl(epoll, EPOLL_CTL_ADD, fd, events, (ulong)fd) < 0)
            throw LastError("epoll_ctl");
    }

    public void Dispose()
    {
        close(wakeFd);
        close(epoll);
    }
}

/// <summary>The loop's main fd and the hooks that run around each wait (for the Wayland read protocol).</summary>
internal interface IEventSource
{
    int Fd { get; }
    void BeforeWait();
    void AfterWait(FdEvents events);
}

/// <summary>Runs an action once on dispose.</summary>
internal sealed class Disposer(Action action) : IDisposable
{
    Action? action = action;

    public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke();
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
