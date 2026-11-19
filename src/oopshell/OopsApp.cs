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
    private readonly EventLoop _loop = new();
    private readonly List<GlobalInfo> _globals = new();
    private readonly Dictionary<string, WlProxy> _boundSingletons = new();
    private readonly WlRegistry _registry;
    private readonly List<PosixSignalRegistration> _signals = new();
    private int _exitCode;
    private bool _disposed;
    private bool _sourceAdded;
    private bool _waitingForWritable;

    private OopsApp(OopsOptions options)
    {
        Options = options;
        SynchronizationContext = new LoopSynchronizationContext(_loop);
        _loop.ErrorHandler = HandleUnhandled;
        WlProxy.UnhandledException = e => Current?.HandleUnhandled(e) == true;
        Display = WlDisplay.Connect(options.Display);
        _registry = Display.GetRegistry();
        _registry.Global += e => OnGlobal(new GlobalInfo(e.Name, e.Interface, e.Version));
        _registry.GlobalRemove += e => OnGlobalRemove(e.Name);
        Display.Roundtrip(); // globals
        Initializing = true;
        Compositor = Get<WlCompositor>();
        Shm = Get<WlShm>();
        foreach (var global in _globals.ToList())
            BindKnownGlobal(global);
        BindBackgroundEffects(); // capabilities arrive in the next roundtrip
        Display.Roundtrip(); // initial output/seat state
        Display.Roundtrip(); // events from objects bound in the previous roundtrip (xdg_output, keymaps)
        Initializing = false;
        Current = this;
    }

    /// <summary>Connects to the compositor and binds the globals oopshell uses.</summary>
    public static OopsApp Connect(OopsOptions? options = null) => new(options ?? new OopsOptions());

    /// <summary>The most recently connected app; hot reload has no other way to reach it.</summary>
    internal static OopsApp? Current { get; private set; }

    internal bool Initializing { get; private set; }

    public OopsOptions Options { get; }

    /// <summary>Posts to this app's UI thread.</summary>
    /// <remarks>Installed as the current context while <see cref="Run"/> runs. Hand it to libraries that call back on other threads.</remarks>
    public SynchronizationContext SynchronizationContext { get; }

    /// <summary>
    /// Raised when an exception escapes a handler: rendering, input, a timer, posted work, an awaited
    /// continuation, or a service event.
    /// </summary>
    /// <remarks>
    /// Unhandled, the exception ends <see cref="Run"/>. Set <see cref="LoopExceptionEventArgs.Handled"/> to keep
    /// the loop running. Connection loss always ends <see cref="Run"/>.
    /// </remarks>
    public event Action<LoopExceptionEventArgs>? UnhandledException;

    /// <summary>The raw connection (escape hatch).</summary>
    public WlDisplay Display { get; }

    public WlCompositor Compositor { get; }

    public WlShm Shm { get; }

    /// <summary>Every global the compositor currently advertises.</summary>
    public IReadOnlyList<GlobalInfo> Globals => _globals;

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
        if (_boundSingletons.TryGetValue(T.InterfaceName, out var existing))
            return (T)existing;
        var global = FindGlobal(T.InterfaceName)
            ?? throw new NotSupportedException($"The compositor does not support {T.InterfaceName}.");
        var proxy = Bind<T>(global);
        _boundSingletons[T.InterfaceName] = proxy;
        return proxy;
    }

    /// <summary><see cref="Get{T}"/>, or null if unsupported.</summary>
    public T? TryGet<T>() where T : WlProxy, IWaylandProxy<T> => Has<T>() ? Get<T>() : null;

    /// <summary>Binds a new instance of any global. The caller owns the result.</summary>
    public T Bind<T>(GlobalInfo global, uint? version = null) where T : WlProxy, IWaylandProxy<T>
    {
        if (global.Interface != T.InterfaceName)
            throw new ArgumentException($"Global {global.Name} is {global.Interface}, not {T.InterfaceName}.");
        uint bound = Math.Min(version ?? global.Version, Math.Min(global.Version, T.MaxVersion));
        return _registry.Bind<T>(global.Name, bound);
    }

    private GlobalInfo? FindGlobal(string iface) => _globals.Find(g => g.Interface == iface);

    private void OnGlobal(GlobalInfo global)
    {
        _globals.Add(global);
        if (!Initializing && Compositor is not null)
            BindKnownGlobal(global);
    }

    private void OnGlobalRemove(uint name)
    {
        int index = _globals.FindIndex(g => g.Name == name);
        if (index < 0)
            return;
        var global = _globals[index];
        _globals.RemoveAt(index);
        RemoveKnownGlobal(global);
    }

    /// <summary>Binds multi-instance globals (outputs, seats) as they appear.</summary>
    private void BindKnownGlobal(GlobalInfo global)
    {
        switch (global.Interface)
        {
            case "wl_output": AddOutput(global); break;
            case "wl_seat": AddSeat(global); break;
        }
    }

    private void RemoveKnownGlobal(GlobalInfo global)
    {
        switch (global.Interface)
        {
            case "wl_output": RemoveOutput(global); break;
            case "wl_seat": RemoveSeat(global); break;
        }
    }

    /// <summary>
    /// Runs the event loop on the calling thread (which becomes the UI thread) until <see cref="Quit"/>,
    /// SIGINT/SIGTERM, or connection loss. Returns the exit code; non-zero after connection loss.
    /// </summary>
    public int Run()
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(SynchronizationContext);
        _signals.Add(PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal));
        _signals.Add(PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal));
        if (!_sourceAdded)
        {
            _loop.AddSource(Display.Fd);
            _sourceAdded = true;
        }
        try
        {
            _loop.Run(this);
        }
        catch (Exception e) when (e is WaylandConnectionException or WaylandProtocolException)
        {
            ConnectionError = e;
            Console.Error.WriteLine($"oopshell: {e.Message}");
            _exitCode = _exitCode == 0 ? 1 : _exitCode;
        }
        finally
        {
            foreach (var signal in _signals)
                signal.Dispose();
            _signals.Clear();
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
        return _exitCode;
    }

    private bool HandleUnhandled(Exception e)
    {
        if (e is WaylandConnectionException or WaylandProtocolException || UnhandledException is null)
            return false;
        var args = new LoopExceptionEventArgs(e);
        UnhandledException.Invoke(args);
        return args.Handled;
    }

    private void OnSignal(PosixSignalContext context)
    {
        context.Cancel = true;
        int code = 128 + (context.Signal == PosixSignal.SIGINT ? 2 : 15);
        Post(() => Quit(code));
    }

    /// <summary>Stops <see cref="Run"/>, which then returns <paramref name="exitCode"/>.</summary>
    public void Quit(int exitCode = 0)
    {
        _exitCode = exitCode;
        _loop.Stop();
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread. Safe from any thread.</summary>
    public void Post(Action action) => _loop.Post(action);

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

    /// <summary>
    /// Calls <paramref name="callback"/> after <paramref name="due"/>, then every <paramref name="period"/> if given.
    /// Dispose to cancel. Measured on the monotonic clock, which stops during suspend: for anything that shows
    /// the time, use <see cref="SetClockTimer"/>.
    /// </summary>
    public IDisposable SetTimer(TimeSpan due, TimeSpan? period, Action callback) => _loop.SetTimer(due, period, callback);

    /// <summary>
    /// Calls <paramref name="callback"/> on every multiple of <paramref name="interval"/> of local wall-clock time:
    /// <c>SetClockTimer(TimeSpan.FromMinutes(1), …)</c> ticks on the minute, for a clock. After a suspend it fires
    /// on resume, and when the system clock is set it fires at once and realigns. Dispose to cancel.
    /// </summary>
    public IDisposable SetClockTimer(TimeSpan interval, Action callback) => _loop.SetClockTimer(interval, callback);

    /// <summary>Calls <paramref name="callback"/> on the UI thread whenever <paramref name="fd"/> is ready. Dispose to stop.</summary>
    public IDisposable WatchFd(int fd, FdEvents events, Action<FdEvents> callback) => _loop.WatchFd(fd, events, callback);

    int IEventSource.Fd => Display.Fd;

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
        if (flushed == _waitingForWritable)
        {
            _waitingForWritable = !flushed;
            _loop.ModifyWatch(Display.Fd, flushed ? FdEvents.Readable : FdEvents.Readable | FdEvents.Writable);
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

    /// <summary>Raised when the app is disposed, before the connection closes.</summary>
    /// <remarks>Add-ons (EGL, D-Bus) release resources tied to the connection here.</remarks>
    public event Action? Disposing;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Disposing?.Invoke();
        foreach (var surface in _surfaces.ToList())
            surface.Dispose();
        if (Current == this)
            Current = null;
        Display.Dispose();
        _loop.Dispose();
    }
}

/// <summary>An exception that escaped a handler (<see cref="OopsApp.UnhandledException"/>).</summary>
public sealed class LoopExceptionEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception;

    /// <summary>Set to keep the event loop running.</summary>
    public bool Handled { get; set; }
}
