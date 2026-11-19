using System.Diagnostics;
using System.Drawing;
using oopshell.Protocol;

namespace oopshell;

/// <summary>
/// Base of every oopshell surface (a <c>wl_surface</c> with a role). oopshell handles configures and frame
/// pacing; you handle <see cref="Configured"/> and <see cref="Render"/>.
/// </summary>
/// <remarks>
/// Lifecycle: the role is set up in the constructor; the initial (bufferless) commit happens in the next
/// render phase, so properties set right after construction are part of it. After the first configure,
/// <see cref="Render"/> is raised whenever the surface is invalidated, at most once per frame callback.
/// </remarks>
public abstract partial class Surface : IDisposable
{
    private readonly WpViewport? _viewport;
    private readonly WpFractionalScaleV1? _fractionalScale;
    private readonly List<Output> _enteredOutputs = new();
    private int _preferredBufferScale;    // from wl_surface.preferred_buffer_scale (v6); 0 = not sent
    private int _fractionalScale120;      // from wp_fractional_scale_v1; 0 = not sent

    private bool _needsInitialCommit = true;
    private bool _dirty;
    private bool _renderNow;              // render in the next phase even if a frame callback is in flight
    private bool _presentedThisFrame;
    private bool _inRender;
    private Rectangle _damage;            // accumulated, in buffer coordinates
    private WlCallback? _frameCallback;
    private Action<WlCallbackDoneEvent>? _onFrameDone; // cached so each frame does not allocate a new delegate
    private uint _lastFrameTime;

    // State last sent to the compositor, so it is only re-sent when it changes.
    private int _sentBufferScale = 1;
    private Size _sentViewportSize;
    private bool _regionsDirty = true;
    private Region? _inputRegion;
    private Region? _opaqueRegion;
    private CursorShape _cursor = CursorShape.Default;

    private protected Surface(OopsApp app)
    {
        App = app;
        Proxy = app.Compositor.CreateSurface();
        Proxy.Enter += e => OnOutputEnter(e.Output);
        Proxy.Leave += e => OnOutputLeave(e.Output);
        Proxy.PreferredBufferScale += e =>
        {
            _preferredBufferScale = e.Factor;
            OnScaleMaybeChanged();
        };

        if (app.TryGet<WpFractionalScaleManagerV1>() is { } fractionalManager && app.TryGet<WpViewporter>() is { } viewporter)
        {
            _viewport = viewporter.GetViewport(Proxy);
            _fractionalScale = fractionalManager.GetFractionalScale(Proxy);
            _fractionalScale.PreferredScale += e =>
            {
                _fractionalScale120 = (int)e.Scale;
                OnScaleMaybeChanged();
            };
        }
        app.AddSurface(this);
    }

    public OopsApp App { get; }

    /// <summary>The raw <c>wl_surface</c> (escape hatch).</summary>
    public WlSurface Proxy { get; }

    /// <summary><c>wl_display*</c>, for EGL/Vulkan/wgpu.</summary>
    public nint NativeDisplay => App.Display.Handle;

    /// <summary><c>wl_surface*</c>, for EGL/Vulkan/wgpu.</summary>
    public nint NativeSurface => Proxy.Handle;

    /// <summary>Size the compositor configured, in logical pixels. Empty until the first configure.</summary>
    public Size LogicalSize { get; private set; }

    /// <summary>Buffer pixels per logical pixel: fractional when the compositor supports it, otherwise an integer.</summary>
    public double Scale { get; private set; } = 1;

    /// <summary>Pixel size of the buffers to present: <see cref="LogicalSize"/> × <see cref="Scale"/>, rounded.</summary>
    public Size BufferSize => ScaleSize(LogicalSize, Scale);

    /// <summary>True once the first configure has been acknowledged.</summary>
    public bool IsConfigured { get; private set; }

    public bool IsDisposed { get; private set; }

    /// <summary>The outputs this surface is (partly) shown on.</summary>
    public IReadOnlyList<Output> Outputs => _enteredOutputs;

    /// <summary>Draw and present exactly once (via a swapchain, EGL, or <see cref="PrepareCommit"/> + your own commit).</summary>
    public event Action<RenderFrame>? Render;

    /// <summary>Raised after each configure and scale change, before the resulting <see cref="Render"/>.</summary>
    public event Action<SurfaceConfiguredEventArgs>? Configured;

    /// <summary>The compositor closed the surface (layer surface closed, popup dismissed, lock finished…).</summary>
    public event Action? Closed;

    public event Action<PointerEvent>? PointerEnter;
    public event Action<PointerEvent>? PointerLeave;
    public event Action<PointerEvent>? PointerMove;
    public event Action<PointerButtonEvent>? PointerButton;
    public event Action<PointerScrollEvent>? PointerScroll;
    public event Action<KeyEvent>? KeyDown;
    public event Action<KeyEvent>? KeyUp;
    public event Action<TextInputEvent>? TextInput;
    public event Action<KeyboardFocusEvent>? KeyboardFocusChanged;
    public event Action<ModifiersEvent>? ModifiersChanged;
    public event Action<TouchEvent>? TouchDown;
    public event Action<TouchEvent>? TouchMove;
    public event Action<TouchEvent>? TouchUp;
    public event Action<TouchEvent>? TouchCancel;

    /// <summary>Where the surface accepts input, in logical coordinates. Null = everywhere; <see cref="Region.Empty"/> = click-through.</summary>
    public Region? InputRegion
    {
        get => _inputRegion;
        set { _inputRegion = value; _regionsDirty = true; Invalidate(Rectangle.Empty); }
    }

    /// <summary>Area that is fully opaque (an optimization hint for the compositor).</summary>
    public Region? OpaqueRegion
    {
        get => _opaqueRegion;
        set { _opaqueRegion = value; _regionsDirty = true; Invalidate(Rectangle.Empty); }
    }

    /// <summary>The pointer cursor shown over this surface.</summary>
    public CursorShape Cursor
    {
        get => _cursor;
        set
        {
            _cursor = value;
            foreach (var seat in App.Seats)
                seat.ApplyCursor(this);
        }
    }

    /// <summary>Requests a new frame that redraws everything.</summary>
    public void Invalidate() => Invalidate(new Rectangle(Point.Empty, LogicalSize), fullBuffer: true);

    /// <summary>Requests a new frame that redraws <paramref name="logicalDamage"/> (logical coordinates).</summary>
    public void Invalidate(Rectangle logicalDamage) => Invalidate(logicalDamage, fullBuffer: false);

    private void Invalidate(Rectangle logicalDamage, bool fullBuffer)
    {
        if (IsDisposed)
            return;
        var bufferDamage = fullBuffer ? new Rectangle(Point.Empty, BufferSize) : ToBuffer(logicalDamage);
        _damage = _damage.IsEmpty ? bufferDamage : bufferDamage.IsEmpty ? _damage : Rectangle.Union(_damage, bufferDamage);
        _dirty = true;
    }

    /// <summary>
    /// Call right before committing a buffer yourself (raw-handle path). Requests the next frame callback and
    /// sends pending surface state (scale, viewport, regions). The swapchain and EGL helpers call this for you.
    /// </summary>
    public void PrepareCommit()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        _presentedThisFrame = true;
        HasPresented = true;
        if (_frameCallback is null)
        {
            _frameCallback = Proxy.Frame();
            _frameCallback.Done += _onFrameDone ??= e => OnFrameDone(e.CallbackData);
        }
        RequestPresentationFeedback();
        SendPendingState();
    }

    private void SendPendingState()
    {
        if (_fractionalScale is not null)
        {
            if (_sentBufferScale != 1 && Proxy.BoundVersion >= 3)
                Proxy.SetBufferScale(1);
            _sentBufferScale = 1;
            if (_sentViewportSize != LogicalSize && !LogicalSize.IsEmpty)
            {
                _viewport!.SetDestination(LogicalSize.Width, LogicalSize.Height);
                _sentViewportSize = LogicalSize;
            }
        }
        else if (_sentBufferScale != (int)Scale && Proxy.BoundVersion >= 3)
        {
            Proxy.SetBufferScale((int)Scale);
            _sentBufferScale = (int)Scale;
        }

        if (_regionsDirty)
        {
            _regionsDirty = false;
            using (var input = _inputRegion?.Create(App.Compositor))
                Proxy.SetInputRegion(input);
            using (var opaque = _opaqueRegion?.Create(App.Compositor))
                Proxy.SetOpaqueRegion(opaque);
        }
        ApplyBlurRegion();
    }

    private void OnFrameDone(uint time)
    {
        _frameCallback?.Dispose();
        _frameCallback = null;
        _lastFrameTime = time;
    }

    /// <summary>Called by the app's render phase.</summary>
    internal void RunRenderPhase()
    {
        if (IsDisposed)
            return;
        if (_needsInitialCommit)
        {
            _needsInitialCommit = false;
            if (CommitsBeforeConfigure)
            {
                BeforeInitialCommit();
                Proxy.Commit();
                return;
            }
        }
        if (!IsConfigured || !_dirty || (_frameCallback is not null && !_renderNow) || BufferSize.IsEmpty)
            return;

        var frame = new RenderFrame(BufferSize, LogicalSize, Scale,
            Rectangle.Intersect(_damage, new Rectangle(Point.Empty, BufferSize)), _lastFrameTime);
        _dirty = false;
        _renderNow = false;
        _damage = Rectangle.Empty;
        _presentedThisFrame = false;
        _inRender = true;
        try
        {
            Render?.Invoke(frame);
        }
        finally
        {
            _inRender = false;
        }
        if (!_presentedThisFrame && Render is not null)
            Debug.WriteLine($"oopshell: {GetType().Name} did not present during Render.");
    }

    internal bool IsRendering => _inRender;

    /// <summary>A buffer has been committed (so a state-only commit keeps the surface mapped).</summary>
    internal bool HasPresented { get; private set; }

    /// <summary>False for roles that are configured without an initial commit (and must not commit before acking).</summary>
    private protected virtual bool CommitsBeforeConfigure => true;

    /// <summary>Last chance for a role to send state before the initial commit (e.g. a popup grab).</summary>
    private protected virtual void BeforeInitialCommit() { }

    /// <summary>Roles call this after acking a configure with the new logical size.</summary>
    private protected void ApplyConfigure(Size logicalSize)
    {
        bool sizeChanged = logicalSize != LogicalSize;
        LogicalSize = logicalSize;
        bool first = !IsConfigured;
        IsConfigured = true;
        Scale = ComputeScale();
        if (first || sizeChanged)
            _sentViewportSize = Size.Empty;
        Configured?.Invoke(new SurfaceConfiguredEventArgs(LogicalSize, Scale, BufferSize));
        // The first frame after a configure is drawn immediately, not after the next frame callback.
        Invalidate();
        _renderNow = true;
    }

    /// <summary>Scale to assume before the compositor says anything (e.g. the layer surface's output).</summary>
    private protected virtual int InitialScaleHint => 1;

    private double ComputeScale()
    {
        if (_fractionalScale is not null && _fractionalScale120 > 0)
            return _fractionalScale120 / 120.0;
        if (_preferredBufferScale > 0)
            return _preferredBufferScale;
        return _enteredOutputs.Count > 0 ? _enteredOutputs.Max(o => o.Scale) : InitialScaleHint;
    }

    private void OnScaleMaybeChanged()
    {
        double scale = ComputeScale();
        if (!IsConfigured || scale == Scale)
            return;
        Scale = scale;
        Configured?.Invoke(new SurfaceConfiguredEventArgs(LogicalSize, Scale, BufferSize));
        Invalidate();
        _renderNow = true;
    }

    private void OnOutputEnter(WlOutput wlOutput)
    {
        if (App.FindOutput(wlOutput) is { } output && !_enteredOutputs.Contains(output))
        {
            _enteredOutputs.Add(output);
            OnScaleMaybeChanged();
        }
    }

    private void OnOutputLeave(WlOutput wlOutput)
    {
        if (App.FindOutput(wlOutput) is { } output && _enteredOutputs.Remove(output))
            OnScaleMaybeChanged();
    }

    internal void OnOutputRemoved(Output output)
    {
        if (_enteredOutputs.Remove(output))
            OnScaleMaybeChanged();
    }

    private protected static Size GrowBy(Size size, Margins m) =>
        new(size.Width + m.Left + m.Right, size.Height + m.Top + m.Bottom);

    /// <summary>Sends xdg_surface.set_window_geometry for margins (applied with the next commit); once set it is kept up to date.</summary>
    private protected static void SendWindowGeometry(XdgSurface xdg, Margins m, Size geometry, ref bool sent)
    {
        if (m == default && !sent)
            return;
        xdg.SetWindowGeometry(m.Left, m.Top, Math.Max(1, geometry.Width), Math.Max(1, geometry.Height));
        sent = true;
    }

    internal static Size ScaleSize(Size logical, double scale) =>
        new((int)Math.Round(logical.Width * scale), (int)Math.Round(logical.Height * scale));

    private Rectangle ToBuffer(Rectangle logical)
    {
        if (logical.IsEmpty)
            return Rectangle.Empty;
        int left = (int)Math.Floor(logical.Left * Scale);
        int top = (int)Math.Floor(logical.Top * Scale);
        int right = (int)Math.Ceiling(logical.Right * Scale);
        int bottom = (int)Math.Ceiling(logical.Bottom * Scale);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    /// <summary>Raises <see cref="Closed"/>. Roles call this when the compositor ends the surface.</summary>
    private protected void RaiseClosed() => Closed?.Invoke();

    internal void RaisePointerEnter(PointerEvent e) => PointerEnter?.Invoke(e);
    internal void RaisePointerLeave(PointerEvent e) => PointerLeave?.Invoke(e);
    internal void RaisePointerMove(PointerEvent e) => PointerMove?.Invoke(e);
    internal void RaisePointerButton(PointerButtonEvent e) => PointerButton?.Invoke(e);
    internal void RaisePointerScroll(PointerScrollEvent e) => PointerScroll?.Invoke(e);
    internal void RaiseKeyDown(KeyEvent e) => KeyDown?.Invoke(e);
    internal void RaiseKeyUp(KeyEvent e) => KeyUp?.Invoke(e);
    internal void RaiseTextInput(TextInputEvent e) => TextInput?.Invoke(e);
    internal void RaiseKeyboardFocus(KeyboardFocusEvent e) => KeyboardFocusChanged?.Invoke(e);
    internal void RaiseModifiersChanged(ModifiersEvent e) => ModifiersChanged?.Invoke(e);
    internal void RaiseTouchDown(TouchEvent e) => TouchDown?.Invoke(e);
    internal void RaiseTouchMove(TouchEvent e) => TouchMove?.Invoke(e);
    internal void RaiseTouchUp(TouchEvent e) => TouchUp?.Invoke(e);
    internal void RaiseTouchCancel(TouchEvent e) => TouchCancel?.Invoke(e);

    /// <summary>Destroys the role objects. The base then destroys the <c>wl_surface</c>.</summary>
    private protected abstract void DestroyRole();

    /// <summary>Popups whose parent is this surface; they are destroyed first, as xdg-shell requires.</summary>
    internal List<Popup> ChildPopups { get; } = new();

    public void Dispose()
    {
        if (IsDisposed)
            return;
        foreach (var popup in ChildPopups.ToList())
            popup.Dispose();
        IsDisposed = true;
        App.RemoveSurface(this);
        DestroyRole();
        _frameCallback?.Dispose();
        _backgroundEffect?.Dispose();
        DisposeInhibitors();
        _fractionalScale?.Dispose();
        _viewport?.Dispose();
        Proxy.Dispose();
    }
}

public sealed partial class OopsApp
{
    private readonly List<Surface> _surfaces = new();
    private readonly Dictionary<WlSurface, Surface> _surfacesByProxy = new();

    /// <summary>Every live oopshell surface.</summary>
    public IReadOnlyList<Surface> Surfaces => _surfaces;

    /// <summary>The oopshell surface for a raw <c>wl_surface</c>, if any.</summary>
    public Surface? FindSurface(WlSurface? proxy) => proxy is not null && _surfacesByProxy.TryGetValue(proxy, out var s) ? s : null;

    internal void AddSurface(Surface surface)
    {
        _surfaces.Add(surface);
        _surfacesByProxy[surface.Proxy] = surface;
    }

    internal void RemoveSurface(Surface surface)
    {
        _surfaces.Remove(surface);
        _surfacesByProxy.Remove(surface.Proxy);
        foreach (var seat in _seats)
            seat.Forget(surface);
    }

    /// <summary>Initial commits and Render events for every surface that needs one.</summary>
    private void RenderPhase()
    {
        // Iterate a copy (render handlers may create or dispose surfaces), reusing one list to avoid garbage.
        _renderScratch.Clear();
        _renderScratch.AddRange(_surfaces);
        foreach (var surface in _renderScratch)
            surface.RunRenderPhase();
    }

    private readonly List<Surface> _renderScratch = new();
}
