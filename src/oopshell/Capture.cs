using System.Drawing;
using System.Runtime.InteropServices;
using oopshell.Protocol;

namespace oopshell;

/// <summary>
/// One captured image: <see cref="Width"/>×<see cref="Height"/> pixels, always B, G, R, A bytes (little-endian
/// <see cref="WlShmFormat.Argb8888"/>, or <see cref="WlShmFormat.Xrgb8888"/> when there is no alpha; alpha
/// premultiplied), <see cref="Stride"/> bytes per row. Compositors that capture in R, G, B order are converted.
/// </summary>
public sealed record CapturedImage(int Width, int Height, int Stride, WlShmFormat Format, byte[] Pixels)
{
    /// <summary>The format has no alpha channel (treat every pixel as opaque).</summary>
    public bool IsOpaque => Format is WlShmFormat.Xrgb8888 or WlShmFormat.Xbgr8888;
}

/// <summary>
/// Captures a window or an output into shared memory (ext-image-copy-capture-v1), for window thumbnails
/// and previews:
/// <code>
/// using var capture = ScreenCapture.ForWindow(app, window);   // null if the compositor cannot
/// var image = await capture.CaptureAsync();
/// </code>
/// A capture keeps its session (and buffer) for repeated <see cref="CaptureAsync"/> calls; dispose it when
/// done. Window capture needs ext-foreign-toplevel-image-capture-source-v1 (not every compositor has it:
/// see <see cref="CanCaptureWindows"/>).
/// </summary>
public sealed class ScreenCapture : IDisposable
{
    private readonly OopsApp _app;
    private readonly ExtImageCaptureSourceV1 _source;
    private readonly ExtImageCopyCaptureSessionV1 _session;
    private readonly List<WlShmFormat> _formats = new();
    private Size _size;
    private Size _pendingSize;
    private List<WlShmFormat> _pendingFormats = new();
    private bool _constraintsKnown;
    private int _constraintsVersion; // bumped by every session.done
    private readonly List<(Action Then, Action<Exception> Fail)> _waiting = new();
    private Task<CapturedImage>? _inFlight;
    private ShmPool? _pool;
    private WlBuffer? _buffer;
    private WlShmFormat _bufferFormat;
    private bool _disposed;

    private ScreenCapture(OopsApp app, ExtImageCaptureSourceV1 source, bool cursor)
    {
        _app = app;
        _source = source;
        _session = app.Get<ExtImageCopyCaptureManagerV1>().CreateSession(source,
            cursor ? ExtImageCopyCaptureManagerV1Options.PaintCursors : 0);
        _session.BufferSize += e => _pendingSize = new Size((int)e.Width, (int)e.Height);
        _session.ShmFormat += e => _pendingFormats.Add(e.Format);
        _session.Done += () =>
        {
            _size = _pendingSize;
            _formats.Clear();
            _formats.AddRange(_pendingFormats);
            _pendingFormats = new List<WlShmFormat>();
            _constraintsVersion++;
            _constraintsKnown = true;
            var ready = _waiting.ToList();
            _waiting.Clear();
            foreach (var (then, _) in ready)
                then();
        };
        _session.Stopped += () =>
        {
            IsStopped = true;
            if (_inFlightFrame is { } pending)
            {
                _inFlightFrame = null;
                pending.Frame.Dispose();
                pending.Fail(new InvalidOperationException("The capture source went away."));
            }
            var failed = _waiting.ToList();
            _waiting.Clear();
            foreach (var (_, fail) in failed)
                fail(new InvalidOperationException("The capture source went away."));
            Stopped?.Invoke();
        };
    }

    /// <summary>True if the compositor can capture other applications' windows.</summary>
    public static bool CanCaptureWindows(OopsApp app) =>
        app.Has<ExtImageCopyCaptureManagerV1>() && app.Has<ExtForeignToplevelImageCaptureSourceManagerV1>() && app.Has<ExtForeignToplevelListV1>();

    /// <summary>True if the compositor can capture outputs.</summary>
    public static bool CanCaptureOutputs(OopsApp app) =>
        app.Has<ExtImageCopyCaptureManagerV1>() && app.Has<ExtOutputImageCaptureSourceManagerV1>();

    /// <summary>
    /// A capture of <paramref name="window"/>, or null if the compositor cannot capture windows (or has not
    /// matched this window to its capture handle yet: try again after the window's next change).
    /// </summary>
    public static ScreenCapture? ForWindow(OopsApp app, ForeignToplevel window, bool cursor = false)
    {
        if (!CanCaptureWindows(app) || window.CaptureHandle is not { } handle)
            return null;
        return new ScreenCapture(app, app.Get<ExtForeignToplevelImageCaptureSourceManagerV1>().CreateSource(handle), cursor);
    }

    /// <summary>A capture of a whole output, or null if the compositor cannot.</summary>
    public static ScreenCapture? ForOutput(OopsApp app, Output output, bool cursor = false)
    {
        if (!CanCaptureOutputs(app))
            return null;
        return new ScreenCapture(app, app.Get<ExtOutputImageCaptureSourceManagerV1>().CreateSource(output.Proxy), cursor);
    }

    /// <summary>The source is gone (window closed, output unplugged); further captures fail.</summary>
    public bool IsStopped { get; private set; }

    public event Action? Stopped;

    /// <summary>
    /// Captures the current contents. Throws <see cref="InvalidOperationException"/> (in the task) if the source
    /// is gone or the compositor refuses. Concurrent calls share one capture. Call on the UI thread.
    /// </summary>
    public Task<CapturedImage> CaptureAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_inFlight is { IsCompleted: false })
            return _inFlight;
        var result = new TaskCompletionSource<CapturedImage>(TaskCreationOptions.RunContinuationsAsynchronously);
        Attempt(result, retry: true);
        return _inFlight = result.Task;
    }

    // Driven by callbacks from Wayland events, not awaits: the session and buffers belong to the UI thread, and
    // continuations of awaits inside a library could resume elsewhere.
    private void Attempt(TaskCompletionSource<CapturedImage> result, bool retry) => WhenConstraintsKnown(() => CaptureFrame(image =>
    {
        if (image is not null)
            result.TrySetResult(image);
        else if (retry)
            Attempt(result, retry: false);   // the size or formats changed under us: once more with the new ones
        else
            result.TrySetException(new InvalidOperationException("The capture source kept changing size."));
    }, error => result.TrySetException(error)), error => result.TrySetException(error));

    private void WhenConstraintsKnown(Action then, Action<Exception> fail)
    {
        if (IsStopped)
            fail(new InvalidOperationException("The capture source went away."));
        else if (_constraintsKnown)
            then();
        else
            _waiting.Add((then, fail));
    }

    /// <summary>Captures one frame; <paramref name="done"/> gets null if the buffer no longer matches the constraints.</summary>
    private void CaptureFrame(Action<CapturedImage?> done, Action<Exception> fail)
    {
        if (_size.Width <= 0 || _size.Height <= 0)
        {
            fail(new InvalidOperationException("The capture source has no size."));
            return;
        }
        // GPU renderers often offer only the R/B-swapped formats; those are converted when copying out.
        WlShmFormat[] preferred = [WlShmFormat.Argb8888, WlShmFormat.Xrgb8888, WlShmFormat.Abgr8888, WlShmFormat.Xbgr8888];
        if (preferred.FirstOrDefault(f => _formats.Contains(f), (WlShmFormat)uint.MaxValue) is var format && format == (WlShmFormat)uint.MaxValue)
        {
            fail(new InvalidOperationException($"The compositor offers no usable shared-memory format for capture (offered: {(_formats.Count == 0 ? "none" : string.Join(", ", _formats))})."));
            return;
        }
        if (_pool is null || _pool.BufferSize != _size || _bufferFormat != format)
        {
            _buffer?.Dispose();
            _pool?.Dispose();
            _pool = new ShmPool(_app.Shm, _size, format);
            _buffer = _pool.CreateBuffer(_pool.AddSlot());
            _bufferFormat = format;
        }

        if (_size.Width is <= 0 or > ShmPool.MaxSide || _size.Height is <= 0 or > ShmPool.MaxSide)
        {
            fail(new InvalidOperationException($"The compositor asked for an unusable buffer size {_size.Width}x{_size.Height}."));
            return;
        }
        var frameSize = _size;
        int version = _constraintsVersion;
        var frame = _session.CreateFrame();
        _inFlightFrame = (frame, fail);
        frame.Ready += () =>
        {
            _inFlightFrame = null;
            if (_disposed)
                return;
            int stride = frameSize.Width * 4;
            var pixels = new byte[stride * frameSize.Height];
            Marshal.Copy(_pool.SlotPointer(0), pixels, 0, pixels.Length);
            frame.Dispose();
            bool swapped = format is WlShmFormat.Abgr8888 or WlShmFormat.Xbgr8888;
            if (swapped)
            {
                for (int i = 0; i < pixels.Length; i += 4)
                    (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            }
            var reported = format switch { WlShmFormat.Abgr8888 => WlShmFormat.Argb8888, WlShmFormat.Xbgr8888 => WlShmFormat.Xrgb8888, _ => format };
            done(new CapturedImage(frameSize.Width, frameSize.Height, stride, reported, pixels));
        };
        frame.Failed += e =>
        {
            _inFlightFrame = null;
            frame.Dispose();
            if (_disposed)
                return;
            if (e.Reason == ExtImageCopyCaptureFrameV1FailureReason.BufferConstraints)
            {
                // New constraints may already have arrived; if not, wait for them.
                if (_constraintsVersion == version)
                    _constraintsKnown = false;
                done(null);
            }
            else
                fail(new InvalidOperationException($"Capture failed: {e.Reason}."));
        };
        frame.AttachBuffer(_buffer!);
        frame.DamageBuffer(0, 0, frameSize.Width, frameSize.Height);
        frame.Capture();
    }

    private (ExtImageCopyCaptureFrameV1 Frame, Action<Exception> Fail)? _inFlightFrame;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // The frame outlives the session in the protocol: destroy it before unmapping the memory it writes to.
        if (_inFlightFrame is { } pending)
        {
            _inFlightFrame = null;
            pending.Frame.Dispose();
            pending.Fail(new ObjectDisposedException(nameof(ScreenCapture)));
        }
        var waitingNow = _waiting.ToList();
        _waiting.Clear();
        foreach (var (_, fail) in waitingNow)
            fail(new ObjectDisposedException(nameof(ScreenCapture)));
        _session.Dispose();
        _source.Dispose();
        _buffer?.Dispose();
        _pool?.Dispose();
    }
}
