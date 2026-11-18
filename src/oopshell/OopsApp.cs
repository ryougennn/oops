using System.Runtime.InteropServices;
using oopshell.Protocol;

namespace oopshell;

/// <summary>A global the compositor advertises in the registry.</summary>
public sealed record GlobalInfo(uint Name, string Interface, uint Version);

/// <summary>Options for <see cref="OopsApp.Connect"/>.</summary>
public sealed record OopsOptions
{
    /// <summary>Display name or socket; null honors <c>WAYLAND_SOCKET</c>/<c>WAYLAND_DISPLAY</c>.</summary>
    public string? Display { get; init; }

    /// <summary>Load a cursor theme via libwayland-cursor when <c>wp_cursor_shape_v1</c> is missing.</summary>
    public bool CursorThemeFallback { get; init; }

    /// <summary>Flash a small marker in a corner of every surface after a hot reload.</summary>
    public bool DevOverlay { get; init; }
}

/// <summary>
/// The connection to the compositor and the UI thread's event loop. Create one with <see cref="Connect"/>,
/// build surfaces, then call <see cref="Run"/>.
/// </summary>
public sealed partial class OopsApp : IDisposable, IEventSource
{
    readonly EventLoop loop = new();
    readonly List<GlobalInfo> globals = new();
    readonly Dictionary<string, WlProxy> boundSingletons = new();
    readonly WlRegistry registry;
    readonly List<PosixSignalRegistration> signals = new();
    int exitCode;
    bool disposed;
    bool sourceAdded;

    OopsApp(OopsOptions options)
    {
        Options = options;
        Display = WlDisplay.Connect(options.Display);
        registry = Display.GetRegistry();
        registry.Global += e => OnGlobal(new GlobalInfo(e.Name, e.Interface, e.Version));
        registry.GlobalRemove += e => OnGlobalRemove(e.Name);
        Display.Roundtrip(); // globals
        Initializing = true;
        Compositor = Get<WlCompositor>();
        Shm = Get<WlShm>();
        foreach (var global in globals.ToList())
            BindKnownGlobal(global);
        Display.Roundtrip(); // initial output/seat state
        Display.Roundtrip(); // events triggered by objects bound in the previous roundtrip (xdg_output, keymaps)
        Initializing = false;
        Current = this;
    }

    /// <summary>Connects to the compositor and binds the globals oopshell uses.</summary>
    public static OopsApp Connect(OopsOptions? options = null) => new(options ?? new OopsOptions());

    /// <summary>The most recently connected app; used by hot reload, which has no other way in.</summary>
    internal static OopsApp? Current { get; private set; }

    internal bool Initializing { get; private set; }

    public OopsOptions Options { get; }

    /// <summary>The raw connection (escape hatch).</summary>
    public WlDisplay Display { get; }

    public WlCompositor Compositor { get; }

    public WlShm Shm { get; }

    /// <summary>Every global the compositor currently advertises.</summary>
    public IReadOnlyList<GlobalInfo> Globals => globals;

    /// <summary>The exception that stopped <see cref="Run"/> because the connection died, if any.</summary>
    public Exception? ConnectionError { get; private set; }

    /// <summary>True if the compositor advertises <typeparamref name="T"/>'s interface.</summary>
    public bool Has<T>() where T : WlProxy, IWaylandProxy<T> => FindGlobal(T.InterfaceName) is not null;

    /// <summary>
    /// The singleton global <typeparamref name="T"/>, bound on first use at
    /// min(advertised, <c>T.MaxVersion</c>). Throws if the compositor lacks it.
    /// </summary>
    public T Get<T>() where T : WlProxy, IWaylandProxy<T>
    {
        if (boundSingletons.TryGetValue(T.InterfaceName, out var existing))
            return (T)existing;
        var global = FindGlobal(T.InterfaceName)
            ?? throw new NotSupportedException($"The compositor does not support {T.InterfaceName}.");
        var proxy = Bind<T>(global);
        boundSingletons[T.InterfaceName] = proxy;
        return proxy;
    }

    /// <summary><see cref="Get{T}"/>, or null if unsupported.</summary>
    public T? TryGet<T>() where T : WlProxy, IWaylandProxy<T> => Has<T>() ? Get<T>() : null;

    /// <summary>Binds a new instance of any global. You own (and dispose) the result.</summary>
    public T Bind<T>(GlobalInfo global, uint? version = null) where T : WlProxy, IWaylandProxy<T>
    {
        if (global.Interface != T.InterfaceName)
            throw new ArgumentException($"Global {global.Name} is {global.Interface}, not {T.InterfaceName}.");
        uint bound = Math.Min(version ?? global.Version, Math.Min(global.Version, T.MaxVersion));
        return registry.Bind<T>(global.Name, bound);
    }

    GlobalInfo? FindGlobal(string iface) => globals.Find(g => g.Interface == iface);

    void OnGlobal(GlobalInfo global)
    {
        globals.Add(global);
        if (!Initializing && Compositor is not null)
            BindKnownGlobal(global);
    }

    void OnGlobalRemove(uint name)
    {
        int index = globals.FindIndex(g => g.Name == name);
        if (index < 0)
            return;
        var global = globals[index];
        globals.RemoveAt(index);
        RemoveKnownGlobal(global);
    }

    /// <summary>Binds multi-instance globals (outputs, seats) as they appear.</summary>
    void BindKnownGlobal(GlobalInfo global)
    {
        switch (global.Interface)
        {
            case "wl_output": AddOutput(global); break;
            case "wl_seat": AddSeat(global); break;
        }
    }

    void RemoveKnownGlobal(GlobalInfo global)
    {
        switch (global.Interface)
        {
            case "wl_output": RemoveOutput(global); break;
            case "wl_seat": RemoveSeat(global); break;
        }
    }

    // ---- event loop -------------------------------------------------------------------------

    /// <summary>
    /// Runs the event loop on the calling thread (which becomes the UI thread) until <see cref="Quit"/>,
    /// SIGINT/SIGTERM, or connection loss. Returns the exit code; non-zero after connection loss.
    /// </summary>
    public int Run()
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new LoopSynchronizationContext(loop));
        signals.Add(PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal));
        signals.Add(PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal));
        if (!sourceAdded)
        {
            loop.AddSource(Display.Fd);
            sourceAdded = true;
        }
        try
        {
            loop.Run(this);
        }
        catch (Exception e) when (e is WaylandConnectionException or WaylandProtocolException)
        {
            ConnectionError = e;
            Console.Error.WriteLine($"oopshell: {e.Message}");
            exitCode = exitCode == 0 ? 1 : exitCode;
        }
        finally
        {
            foreach (var signal in signals)
                signal.Dispose();
            signals.Clear();
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
        return exitCode;
    }

    void OnSignal(PosixSignalContext context)
    {
        context.Cancel = true;
        int code = 128 + (context.Signal == PosixSignal.SIGINT ? 2 : 15);
        Post(() => Quit(code));
    }

    /// <summary>Stops <see cref="Run"/>, which then returns <paramref name="exitCode"/>.</summary>
    public void Quit(int exitCode = 0)
    {
        this.exitCode = exitCode;
        loop.Stop();
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread. Safe from any thread.</summary>
    public void Post(Action action) => loop.Post(action);

    /// <summary>Runs async work on the UI thread and completes when it does. Safe from any thread.</summary>
    public Task InvokeAsync(Func<Task> work)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async () =>
        {
            try
            {
                await work();
                done.SetResult();
            }
            catch (Exception e)
            {
                done.SetException(e);
            }
        });
        return done.Task;
    }

    /// <summary>Calls <paramref name="callback"/> after <paramref name="due"/>, then every <paramref name="period"/> if given. Dispose to cancel.</summary>
    public IDisposable SetTimer(TimeSpan due, TimeSpan? period, Action callback) => loop.SetTimer(due, period, callback);

    /// <summary>Calls <paramref name="callback"/> on the UI thread whenever <paramref name="fd"/> is ready. Dispose to stop.</summary>
    public IDisposable WatchFd(int fd, FdEvents events, Action<FdEvents> callback) => loop.WatchFd(fd, events, callback);

    int IEventSource.Fd => Display.Fd;

    bool waitingForWritable;

    void IEventSource.BeforeWait()
    {
        Display.DispatchPending();
        RenderPhase();
        while (!Display.PrepareRead())
        {
            Display.DispatchPending();
            RenderPhase();
        }
        bool flushed = Display.Flush();
        if (flushed == waitingForWritable)
        {
            waitingForWritable = !flushed;
            loop.ModifyWatch(Display.Fd, flushed ? FdEvents.Readable : FdEvents.Readable | FdEvents.Writable);
        }
    }

    void IEventSource.AfterWait(FdEvents events)
    {
        if ((events & (FdEvents.Readable | FdEvents.Error | FdEvents.HangUp)) != 0)
            Display.ReadEvents();
        else
            Display.CancelRead();
        Display.DispatchPending();
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        if (Current == this)
            Current = null;
        Display.Dispose();
        loop.Dispose();
    }
}
