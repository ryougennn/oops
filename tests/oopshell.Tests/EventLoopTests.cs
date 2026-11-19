using oopshell.Native;

namespace oopshell.Tests;

public class EventLoopTests
{
    /// <summary>Stands in for the Wayland connection: a pipe nobody writes to.</summary>
    sealed unsafe class IdleSource : IEventSource, IDisposable
    {
        readonly int readFd, writeFd;

        public IdleSource()
        {
            int* fds = stackalloc int[2];
            if (LibC.pipe2(fds, LibC.O_CLOEXEC) < 0)
                throw LibC.LastError("pipe2");
            readFd = fds[0];
            writeFd = fds[1];
        }

        public int Fd => readFd;
        public int Waits { get; private set; }
        public void BeforeWait() => Waits++;
        public void AfterWait(FdEvents events) { }

        public void Dispose()
        {
            LibC.close(readFd);
            LibC.close(writeFd);
        }
    }

    static void RunWithTimeout(EventLoop loop, IEventSource source, int timeoutMs = 5000)
    {
        using var timeout = loop.SetTimer(TimeSpan.FromMilliseconds(timeoutMs), null, () => throw new TimeoutException("Loop did not stop."));
        loop.Run(source);
    }

    [Fact]
    public void Posted_work_runs_on_the_loop_thread()
    {
        using var loop = new EventLoop();
        using var source = new IdleSource();
        loop.AddSource(source.Fd);
        int loopThread = Environment.CurrentManagedThreadId;
        int ranOn = -1;

        var poster = new Thread(() => loop.Post(() =>
        {
            ranOn = Environment.CurrentManagedThreadId;
            loop.Stop();
        }));
        poster.Start();
        RunWithTimeout(loop, source);
        poster.Join();

        Assert.Equal(loopThread, ranOn);
    }

    [Fact]
    public void One_shot_and_periodic_timers_fire()
    {
        using var loop = new EventLoop();
        using var source = new IdleSource();
        loop.AddSource(source.Fd);
        int periodic = 0;
        bool oneShot = false;

        using var tick = loop.SetTimer(TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5), () =>
        {
            if (++periodic == 3)
                loop.Stop();
        });
        loop.SetTimer(TimeSpan.Zero, null, () => oneShot = true);
        RunWithTimeout(loop, source);

        Assert.True(oneShot);
        Assert.Equal(3, periodic);
    }

    [Fact]
    public void Disposed_timer_does_not_fire()
    {
        using var loop = new EventLoop();
        using var source = new IdleSource();
        loop.AddSource(source.Fd);
        bool fired = false;

        var timer = loop.SetTimer(TimeSpan.FromMilliseconds(10), null, () => fired = true);
        timer.Dispose();
        loop.SetTimer(TimeSpan.FromMilliseconds(40), null, loop.Stop);
        RunWithTimeout(loop, source);

        Assert.False(fired);
    }

    [Fact]
    public unsafe void WatchFd_reports_readable()
    {
        using var loop = new EventLoop();
        using var source = new IdleSource();
        loop.AddSource(source.Fd);
        int* fds = stackalloc int[2];
        Assert.Equal(0, LibC.pipe2(fds, LibC.O_CLOEXEC | LibC.O_NONBLOCK));
        int readFd = fds[0], writeFd = fds[1];
        FdEvents seen = FdEvents.None;

        using var watch = loop.WatchFd(readFd, FdEvents.Readable, events =>
        {
            seen = events;
            loop.Stop();
        });
        new Thread(() =>
        {
            byte b = 42;
            LibC.write(writeFd, &b, 1);
        }).Start();
        RunWithTimeout(loop, source);

        Assert.True(seen.HasFlag(FdEvents.Readable));
        LibC.close(readFd);
        LibC.close(writeFd);
    }

    [Fact]
    public void Await_resumes_on_the_loop_thread()
    {
        using var loop = new EventLoop();
        using var source = new IdleSource();
        loop.AddSource(source.Fd);
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new LoopSynchronizationContext(loop));
        int loopThread = Environment.CurrentManagedThreadId;
        int resumedOn = -1;

        async void Work()
        {
            await Task.Run(() => Thread.Sleep(10));
            resumedOn = Environment.CurrentManagedThreadId;
            loop.Stop();
        }
        loop.Post(Work);
        try
        {
            RunWithTimeout(loop, source);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        Assert.Equal(loopThread, resumedOn);
    }

    [Fact]
    public void Idle_loop_blocks_instead_of_spinning()
    {
        using var loop = new EventLoop();
        using var source = new IdleSource();
        loop.AddSource(source.Fd);

        loop.SetTimer(TimeSpan.FromMilliseconds(100), null, loop.Stop);
        RunWithTimeout(loop, source);

        // One wait until the timer fires, maybe one more for the stop wake-up: never a busy loop.
        Assert.InRange(source.Waits, 1, 3);
    }

    [Fact]
    public void Clock_timers_tick_on_wall_clock_boundaries()
    {
        using var loop = new EventLoop();
        using var source = new IdleSource();
        loop.AddSource(source.Fd);
        var ticks = new List<DateTime>();
        using var timer = loop.SetClockTimer(TimeSpan.FromMilliseconds(200), () =>
        {
            ticks.Add(DateTime.Now);
            if (ticks.Count == 4)
                loop.Stop();
        });
        RunWithTimeout(loop, source);

        // Each tick lands just after a multiple of 200 ms of the clock (not 200 ms after the timer was made).
        // The first can be late here: its deadline may pass before the loop starts running.
        foreach (var tick in ticks.Skip(1))
            Assert.InRange(tick.TimeOfDay.Ticks % TimeSpan.FromMilliseconds(200).Ticks, 0, TimeSpan.FromMilliseconds(40).Ticks);
        Assert.Throws<ArgumentOutOfRangeException>(() => loop.SetClockTimer(TimeSpan.Zero, () => { }));
    }

    [Fact]
    public void Callback_exceptions_go_to_the_error_handler()
    {
        using var loop = new EventLoop();
        using var source = new IdleSource();
        loop.AddSource(source.Fd);
        var seen = new List<string>();
        loop.ErrorHandler = e => { seen.Add(e.Message); return e.Message != "fatal"; };
        loop.Post(() => throw new InvalidOperationException("posted"));
        loop.SetTimer(TimeSpan.FromMilliseconds(10), null, () => throw new InvalidOperationException("timer"));
        loop.SetTimer(TimeSpan.FromMilliseconds(50), null, loop.Stop);
        RunWithTimeout(loop, source);
        Assert.Equal(["posted", "timer"], seen); // handled: the loop went on to the stop timer

        loop.Post(() => throw new InvalidOperationException("fatal"));
        Assert.Throws<InvalidOperationException>(() => RunWithTimeout(loop, source)); // not handled: it propagates
    }
}
