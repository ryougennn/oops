using System.Collections.ObjectModel;
using System.Drawing;
using System.Runtime.InteropServices;
using oopshell.Protocol;

namespace oopshell;

/// <summary>
/// Another application's window, for taskbars. Backed by wlr-foreign-toplevel-management (with actions) or
/// ext-foreign-toplevel-list (read-only: <see cref="CanActivate"/> is false and actions do nothing).
/// </summary>
public sealed class ForeignToplevel
{
    private readonly OopsApp _app;
    private readonly ZwlrForeignToplevelHandleV1? _wlr;
    private ExtForeignToplevelHandleV1? _ext;
    private readonly List<Output> _outputs = new();

    internal ForeignToplevel(OopsApp app, ZwlrForeignToplevelHandleV1 handle)
    {
        _app = app;
        _wlr = handle;
        handle.Title += e => Title = e.Title;
        handle.AppId += e => AppId = e.AppId;
        handle.OutputEnter += e =>
        {
            if (app.FindOutput(e.Output) is { } o && !_outputs.Contains(o))
                _outputs.Add(o);
        };
        handle.OutputLeave += e =>
        {
            if (app.FindOutput(e.Output) is { } o)
                _outputs.Remove(o);
        };
        handle.State += e =>
        {
            var states = MemoryMarshal.Cast<byte, uint>(e.State);
            IsMaximized = states.Contains((uint)ZwlrForeignToplevelHandleV1State.Maximized);
            IsMinimized = states.Contains((uint)ZwlrForeignToplevelHandleV1State.Minimized);
            IsActivated = states.Contains((uint)ZwlrForeignToplevelHandleV1State.Activated);
            IsFullscreen = states.Contains((uint)ZwlrForeignToplevelHandleV1State.Fullscreen);
        };
        handle.Done += OnDone;
        handle.Closed += OnClosed;
    }

    internal ForeignToplevel(OopsApp app, ExtForeignToplevelHandleV1 handle)
    {
        _app = app;
        _ext = handle;
        handle.Title += e => Title = e.Title;
        handle.AppId += e => AppId = e.AppId;
        handle.Identifier += e => Identifier = e.Identifier;
        handle.Done += OnDone;
        handle.Closed += OnClosed;
    }

    public string Title { get; private set; } = "";
    public string AppId { get; private set; } = "";
    /// <summary>Stable identifier (ext-foreign-toplevel-list only).</summary>
    public string? Identifier { get; private set; }
    public bool IsActivated { get; private set; }
    public bool IsMinimized { get; private set; }
    public bool IsMaximized { get; private set; }
    public bool IsFullscreen { get; private set; }
    public IReadOnlyList<Output> Outputs => _outputs;

    /// <summary>The ext-foreign-toplevel handle, for capture (paired with the wlr handle when both protocols are in use).</summary>
    internal ExtForeignToplevelHandleV1? CaptureHandle => _ext;

    internal void AttachCaptureHandle(ExtForeignToplevelHandleV1? handle) => _ext = handle;

    /// <summary>False with the read-only ext-foreign-toplevel-list fallback.</summary>
    public bool CanActivate => _wlr is not null;

    /// <summary>Raised after a batch of property changes.</summary>
    public event Action? Changed;

    /// <summary>The window is gone. The object is dead afterwards.</summary>
    public event Action? Closed;

    internal event Action<ForeignToplevel>? FirstDone;
    internal event Action<ForeignToplevel>? Removed;
    private bool _announced;

    public void Activate(Seat seat) => _wlr?.Activate(seat.Proxy);
    public void Minimize() => _wlr?.SetMinimized();
    public void Unminimize() => _wlr?.UnsetMinimized();
    public void Maximize() => _wlr?.SetMaximized();
    public void Unmaximize() => _wlr?.UnsetMaximized();
    public void Close() => _wlr?.Close();

    /// <summary>Tells the compositor where this window's taskbar button is (for minimize animations).</summary>
    public void SetRectangle(Surface surface, Rectangle rectangle) =>
        _wlr?.SetRectangle(surface.Proxy, rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);

    private void OnDone()
    {
        if (!_announced)
        {
            _announced = true;
            FirstDone?.Invoke(this);
        }
        else
        {
            Changed?.Invoke();
        }
    }

    private void OnClosed()
    {
        Removed?.Invoke(this);
        Closed?.Invoke();
        _wlr?.Dispose();
        _ext?.Dispose();
    }

    public override string ToString() => $"{AppId}: {Title}";
}

public sealed partial class OopsApp
{
    private ObservableCollection<ForeignToplevel>? _toplevels;
    private ReadOnlyObservableCollection<ForeignToplevel>? _toplevelsView;
    private bool _toplevelsBound;

    /// <summary>
    /// Other applications' windows (for a taskbar), or null if the compositor offers no foreign toplevel
    /// protocol. The list fills asynchronously once accessed for the first time.
    /// </summary>
    public ReadOnlyObservableCollection<ForeignToplevel>? Toplevels
    {
        get
        {
            if (!_toplevelsBound)
            {
                _toplevelsBound = true;
                BindToplevels();
            }
            return _toplevelsView;
        }
    }

    private void BindToplevels()
    {
        var list = new ObservableCollection<ForeignToplevel>();
        void Track(ForeignToplevel toplevel)
        {
            toplevel.FirstDone += t => list.Add(t);
            toplevel.Removed += t => list.Remove(t);
        }

        if (TryGet<ZwlrForeignToplevelManagerV1>() is { } wlr)
        {
            wlr.Toplevel += e => Track(new ForeignToplevel(this, e.Toplevel));
            if (Has<ExtForeignToplevelImageCaptureSourceManagerV1>() && TryGet<ExtForeignToplevelListV1>() is { } captureList)
                PairCaptureHandles(list, captureList);
        }
        else if (TryGet<ExtForeignToplevelListV1>() is { } ext)
            ext.Toplevel += e => Track(new ForeignToplevel(this, e.Toplevel));
        else
            return;
        _toplevels = list;
        _toplevelsView = new ReadOnlyObservableCollection<ForeignToplevel>(list);
    }

    private sealed class CaptureHandle(ExtForeignToplevelHandleV1 handle)
    {
        public readonly ExtForeignToplevelHandleV1 Handle = handle;
        public string Title = "", AppId = "";
        public bool Done;
        public ForeignToplevel? Owner;
    }

    /// <summary>
    /// Window capture needs ext-foreign-toplevel handles, while actions need the wlr ones: bind both lists and
    /// pair each ext handle with the wlr window that has the same app id and title (both lists announce a
    /// window when it maps, so identical windows pair in order).
    /// </summary>
    private static void PairCaptureHandles(ObservableCollection<ForeignToplevel> windows, ExtForeignToplevelListV1 extList)
    {
        var unpaired = new List<CaptureHandle>();
        void Pair()
        {
            foreach (var entry in unpaired.Where(e => e.Done).ToList())
            {
                if (windows.FirstOrDefault(w => w.CaptureHandle is null && w.AppId == entry.AppId && w.Title == entry.Title) is not { } window)
                    continue;
                window.AttachCaptureHandle(entry.Handle);
                window.Closed += () => entry.Handle.Dispose();
                entry.Owner = window;
                unpaired.Remove(entry);
            }
        }
        extList.Toplevel += e =>
        {
            var entry = new CaptureHandle(e.Toplevel);
            e.Toplevel.Title += t => entry.Title = t.Title;
            e.Toplevel.AppId += a => entry.AppId = a.AppId;
            e.Toplevel.Done += () =>
            {
                entry.Done = true;
                if (entry.Owner is null)
                    Pair();
            };
            e.Toplevel.Closed += () =>
            {
                unpaired.Remove(entry);
                entry.Owner?.AttachCaptureHandle(null);
                entry.Handle.Dispose();
            };
            unpaired.Add(entry);
        };
        ((System.Collections.Specialized.INotifyCollectionChanged)windows).CollectionChanged += (_, args) =>
        {
            foreach (ForeignToplevel window in args.NewItems ?? Array.Empty<ForeignToplevel>())
                window.Changed += Pair;
            Pair();
        };
    }
}
