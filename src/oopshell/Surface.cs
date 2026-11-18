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
    readonly WpViewport? viewport;
    readonly WpFractionalScaleV1? fractionalScale;
    readonly List<Output> enteredOutputs = new();
    int preferredBufferScale;    // from wl_surface.preferred_buffer_scale (v6); 0 = not sent
    int fractionalScale120;      // from wp_fractional_scale_v1; 0 = not sent

    bool needsInitialCommit = true;
    bool dirty;
    bool renderNow;              // render in the next phase even if a frame callback is in flight
    bool presentedThisFrame;
    bool inRender;
    Rectangle damage;            // accumulated, in buffer coordinates
    WlCallback? frameCallback;
    Action<WlCallbackDoneEvent>? onFrameDone; // cached so each frame does not allocate a new delegate
    uint lastFrameTime;

    // State last sent to the compositor, so it is only re-sent when it changes.
    int sentBufferScale = 1;
    Size sentViewportSize;
    bool regionsDirty = true;
    Region? inputRegion;
    Region? opaqueRegion;
    CursorShape cursor = CursorShape.Default;

    private protected Surface(OopsApp app)
    {
        App = app;
        Proxy = app.Compositor.CreateSurface();
        Proxy.Enter += e => OnOutputEnter(e.Output);
        Proxy.Leave += e => OnOutputLeave(e.Output);
        Proxy.PreferredBufferScale += e =>
        {
            preferredBufferScale = e.Factor;
            OnScaleMaybeChanged();
        };

        if (app.TryGet<WpFractionalScaleManagerV1>() is { } fractionalManager && app.TryGet<WpViewporter>() is { } viewporter)
        {
            viewport = viewporter.GetViewport(Proxy);
            fractionalScale = fractionalManager.GetFractionalScale(Proxy);
            fractionalScale.PreferredScale += e =>
            {
                fractionalScale120 = (int)e.Scale;
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
    public IReadOnlyList<Output> Outputs => enteredOutputs;

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
        get => inputRegion;
        set { inputRegion = value; regionsDirty = true; Invalidate(Rectangle.Empty); }
    }

    /// <summary>Area that is fully opaque (an optimization hint for the compositor).</summary>
    public Region? OpaqueRegion
    {
        get => opaqueRegion;
        set { opaqueRegion = value; regionsDirty = true; Invalidate(Rectangle.Empty); }
    }

    /// <summary>The pointer cursor shown over this surface.</summary>
    public CursorShape Cursor
    {
        get => cursor;
        set
        {
            cursor = value;
            foreach (var seat in App.Seats)
                seat.ApplyCursor(this);
        }
    }

    /// <summary>Requests a new frame that redraws everything.</summary>
    public void Invalidate() => Invalidate(new Rectangle(Point.Empty, LogicalSize), fullBuffer: true);

    /// <summary>Requests a new frame that redraws <paramref name="logicalDamage"/> (logical coordinates).</summary>
    public void Invalidate(Rectangle logicalDamage) => Invalidate(logicalDamage, fullBuffer: false);

    void Invalidate(Rectangle logicalDamage, bool fullBuffer)
    {
        if (IsDisposed)
            return;
        var bufferDamage = fullBuffer ? new Rectangle(Point.Empty, BufferSize) : ToBuffer(logicalDamage);
        damage = damage.IsEmpty ? bufferDamage : bufferDamage.IsEmpty ? damage : Rectangle.Union(damage, bufferDamage);
        dirty = true;
    }

    /// <summary>
    /// Call right before committing a buffer yourself (raw-handle path). Requests the next frame callback and
    /// sends pending surface state (scale, viewport, regions). The swapchain and EGL helpers call this for you.
    /// </summary>
    public void PrepareCommit()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        presentedThisFrame = true;
        if (frameCallback is null)
        {
            frameCallback = Proxy.Frame();
            frameCallback.Done += onFrameDone ??= e => OnFrameDone(e.CallbackData);
        }
        RequestPresentationFeedback();
        SendPendingState();
    }

    void SendPendingState()
    {
        if (fractionalScale is not null)
        {
            if (sentBufferScale != 1 && Proxy.BoundVersion >= 3)
                Proxy.SetBufferScale(1);
            sentBufferScale = 1;
            if (sentViewportSize != LogicalSize && !LogicalSize.IsEmpty)
            {
                viewport!.SetDestination(LogicalSize.Width, LogicalSize.Height);
                sentViewportSize = LogicalSize;
            }
        }
        else if (sentBufferScale != (int)Scale && Proxy.BoundVersion >= 3)
        {
            Proxy.SetBufferScale((int)Scale);
            sentBufferScale = (int)Scale;
        }

        if (regionsDirty)
        {
            regionsDirty = false;
            using (var input = inputRegion?.Create(App.Compositor))
                Proxy.SetInputRegion(input);
            using (var opaque = opaqueRegion?.Create(App.Compositor))
                Proxy.SetOpaqueRegion(opaque);
        }
    }

    void OnFrameDone(uint time)
    {
        frameCallback?.Dispose();
        frameCallback = null;
        lastFrameTime = time;
    }

    /// <summary>Called by the app's render phase.</summary>
    internal void RunRenderPhase()
    {
        if (IsDisposed)
            return;
        if (needsInitialCommit)
        {
            needsInitialCommit = false;
            BeforeInitialCommit();
            Proxy.Commit();
            return;
        }
        if (!IsConfigured || !dirty || (frameCallback is not null && !renderNow) || BufferSize.IsEmpty)
            return;

        var frame = new RenderFrame(BufferSize, LogicalSize, Scale,
            Rectangle.Intersect(damage, new Rectangle(Point.Empty, BufferSize)), lastFrameTime);
        dirty = false;
        renderNow = false;
        damage = Rectangle.Empty;
        presentedThisFrame = false;
        inRender = true;
        try
        {
            Render?.Invoke(frame);
        }
        finally
        {
            inRender = false;
        }
        if (!presentedThisFrame && Render is not null)
            Debug.WriteLine($"oopshell: {GetType().Name} did not present during Render.");
    }

    internal bool IsRendering => inRender;

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
            sentViewportSize = Size.Empty;
        Configured?.Invoke(new SurfaceConfiguredEventArgs(LogicalSize, Scale, BufferSize));
        // The first frame after a configure is drawn immediately, not after the next frame callback.
        Invalidate();
        renderNow = true;
    }

    /// <summary>Scale to assume before the compositor says anything (e.g. the layer surface's output).</summary>
    private protected virtual int InitialScaleHint => 1;

    double ComputeScale()
    {
        if (fractionalScale is not null && fractionalScale120 > 0)
            return fractionalScale120 / 120.0;
        if (preferredBufferScale > 0)
            return preferredBufferScale;
        return enteredOutputs.Count > 0 ? enteredOutputs.Max(o => o.Scale) : InitialScaleHint;
    }

    void OnScaleMaybeChanged()
    {
        double scale = ComputeScale();
        if (!IsConfigured || scale == Scale)
            return;
        Scale = scale;
        Configured?.Invoke(new SurfaceConfiguredEventArgs(LogicalSize, Scale, BufferSize));
        Invalidate();
        renderNow = true;
    }

    void OnOutputEnter(WlOutput wlOutput)
    {
        if (App.FindOutput(wlOutput) is { } output && !enteredOutputs.Contains(output))
        {
            enteredOutputs.Add(output);
            OnScaleMaybeChanged();
        }
    }

    void OnOutputLeave(WlOutput wlOutput)
    {
        if (App.FindOutput(wlOutput) is { } output && enteredOutputs.Remove(output))
            OnScaleMaybeChanged();
    }

    internal void OnOutputRemoved(Output output)
    {
        if (enteredOutputs.Remove(output))
            OnScaleMaybeChanged();
    }

    internal static Size ScaleSize(Size logical, double scale) =>
        new((int)Math.Round(logical.Width * scale), (int)Math.Round(logical.Height * scale));

    Rectangle ToBuffer(Rectangle logical)
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
        frameCallback?.Dispose();
        fractionalScale?.Dispose();
        viewport?.Dispose();
        Proxy.Dispose();
    }
}

public sealed partial class OopsApp
{
    readonly List<Surface> surfaces = new();
    readonly Dictionary<WlSurface, Surface> surfacesByProxy = new();

    /// <summary>Every live oopshell surface.</summary>
    public IReadOnlyList<Surface> Surfaces => surfaces;

    /// <summary>The oopshell surface for a raw <c>wl_surface</c>, if any.</summary>
    public Surface? FindSurface(WlSurface? proxy) => proxy is not null && surfacesByProxy.TryGetValue(proxy, out var s) ? s : null;

    internal void AddSurface(Surface surface)
    {
        surfaces.Add(surface);
        surfacesByProxy[surface.Proxy] = surface;
    }

    internal void RemoveSurface(Surface surface)
    {
        surfaces.Remove(surface);
        surfacesByProxy.Remove(surface.Proxy);
        foreach (var seat in seats)
            seat.Forget(surface);
    }

    /// <summary>Initial commits and Render events for every surface that needs one.</summary>
    void RenderPhase()
    {
        // Iterate a copy (render handlers may create or dispose surfaces), reusing one list to avoid garbage.
        renderScratch.Clear();
        renderScratch.AddRange(surfaces);
        foreach (var surface in renderScratch)
            surface.RunRenderPhase();
    }

    readonly List<Surface> renderScratch = new();
}
