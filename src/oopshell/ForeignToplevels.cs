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
    readonly OopsApp app;
    readonly ZwlrForeignToplevelHandleV1? wlr;
    readonly ExtForeignToplevelHandleV1? ext;
    readonly List<Output> outputs = new();

    internal ForeignToplevel(OopsApp app, ZwlrForeignToplevelHandleV1 handle)
    {
        this.app = app;
        wlr = handle;
        handle.Title += e => Title = e.Title;
        handle.AppId += e => AppId = e.AppId;
        handle.OutputEnter += e =>
        {
            if (app.FindOutput(e.Output) is { } o && !outputs.Contains(o))
                outputs.Add(o);
        };
        handle.OutputLeave += e =>
        {
            if (app.FindOutput(e.Output) is { } o)
                outputs.Remove(o);
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
        this.app = app;
        ext = handle;
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
    public IReadOnlyList<Output> Outputs => outputs;

    /// <summary>False with the read-only ext-foreign-toplevel-list fallback.</summary>
    public bool CanActivate => wlr is not null;

    /// <summary>Raised after a batch of property changes.</summary>
    public event Action? Changed;

    /// <summary>The window is gone. The object is dead afterwards.</summary>
    public event Action? Closed;

    internal event Action<ForeignToplevel>? FirstDone;
    internal event Action<ForeignToplevel>? Removed;
    bool announced;

    public void Activate(Seat seat) => wlr?.Activate(seat.Proxy);
    public void Minimize() => wlr?.SetMinimized();
    public void Unminimize() => wlr?.UnsetMinimized();
    public void Maximize() => wlr?.SetMaximized();
    public void Unmaximize() => wlr?.UnsetMaximized();
    public void Close() => wlr?.Close();

    /// <summary>Tells the compositor where this window's taskbar button is (for minimize animations).</summary>
    public void SetRectangle(Surface surface, Rectangle rectangle) =>
        wlr?.SetRectangle(surface.Proxy, rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);

    void OnDone()
    {
        if (!announced)
        {
            announced = true;
            FirstDone?.Invoke(this);
        }
        else
        {
            Changed?.Invoke();
        }
    }

    void OnClosed()
    {
        Removed?.Invoke(this);
        Closed?.Invoke();
        wlr?.Dispose();
        ext?.Dispose();
    }

    public override string ToString() => $"{AppId}: {Title}";
}

public sealed partial class OopsApp
{
    ObservableCollection<ForeignToplevel>? toplevels;
    ReadOnlyObservableCollection<ForeignToplevel>? toplevelsView;
    bool toplevelsBound;

    /// <summary>
    /// Other applications' windows (for a taskbar), or null if the compositor offers no foreign toplevel
    /// protocol. The list fills asynchronously once accessed for the first time.
    /// </summary>
    public ReadOnlyObservableCollection<ForeignToplevel>? Toplevels
    {
        get
        {
            if (!toplevelsBound)
            {
                toplevelsBound = true;
                BindToplevels();
            }
            return toplevelsView;
        }
    }

    void BindToplevels()
    {
        var list = new ObservableCollection<ForeignToplevel>();
        void Track(ForeignToplevel toplevel)
        {
            toplevel.FirstDone += t => list.Add(t);
            toplevel.Removed += t => list.Remove(t);
        }

        if (TryGet<ZwlrForeignToplevelManagerV1>() is { } wlr)
            wlr.Toplevel += e => Track(new ForeignToplevel(this, e.Toplevel));
        else if (TryGet<ExtForeignToplevelListV1>() is { } ext)
            ext.Toplevel += e => Track(new ForeignToplevel(this, e.Toplevel));
        else
            return;
        toplevels = list;
        toplevelsView = new ReadOnlyObservableCollection<ForeignToplevel>(list);
    }
}
