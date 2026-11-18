using System.Drawing;
using System.Runtime.InteropServices;
using oopshell.Protocol;

namespace oopshell;

/// <summary>
/// One captured image: <see cref="Width"/>×<see cref="Height"/> pixels in a 32-bit <c>wl_shm</c> format
/// (<see cref="WlShmFormat.Argb8888"/>/<see cref="WlShmFormat.Xrgb8888"/> are B, G, R, A bytes on
/// little-endian machines, alpha premultiplied), <see cref="Stride"/> bytes per row.
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
    readonly OopsApp app;
    readonly ExtImageCaptureSourceV1 source;
    readonly ExtImageCopyCaptureSessionV1 session;
    readonly List<WlShmFormat> formats = new();
    Size size;
    Size pendingSize;
    List<WlShmFormat> pendingFormats = new();
    bool constraintsKnown;
    int constraintsVersion; // bumped by every session.done
    readonly List<(Action Then, Action<Exception> Fail)> waiting = new();
    Task<CapturedImage>? inFlight;
    ShmPool? pool;
    WlBuffer? buffer;
    WlShmFormat bufferFormat;
    bool disposed;

    ScreenCapture(OopsApp app, ExtImageCaptureSourceV1 source, bool cursor)
    {
        this.app = app;
        this.source = source;
        session = app.Get<ExtImageCopyCaptureManagerV1>().CreateSession(source,
            cursor ? ExtImageCopyCaptureManagerV1Options.PaintCursors : 0);
        session.BufferSize += e => pendingSize = new Size((int)e.Width, (int)e.Height);
        session.ShmFormat += e => pendingFormats.Add(e.Format);
        session.Done += () =>
        {
            size = pendingSize;
            formats.Clear();
            formats.AddRange(pendingFormats);
            pendingFormats = new List<WlShmFormat>();
            constraintsVersion++;
            constraintsKnown = true;
            var ready = waiting.ToList();
            waiting.Clear();
            foreach (var (then, _) in ready)
                then();
        };
        session.Stopped += () =>
        {
            IsStopped = true;
            var failed = waiting.ToList();
            waiting.Clear();
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
        ObjectDisposedException.ThrowIf(disposed, this);
        if (inFlight is { IsCompleted: false })
            return inFlight;
        var result = new TaskCompletionSource<CapturedImage>(TaskCreationOptions.RunContinuationsAsynchronously);
        Attempt(result, retry: true);
        return inFlight = result.Task;
    }

    // Driven by callbacks from Wayland events, not awaits: the session and buffers belong to the UI thread, and
    // continuations of awaits inside a library could resume elsewhere.
    void Attempt(TaskCompletionSource<CapturedImage> result, bool retry) => WhenConstraintsKnown(() => CaptureFrame(image =>
    {
        if (image is not null)
            result.TrySetResult(image);
        else if (retry)
            Attempt(result, retry: false);   // the size or formats changed under us: once more with the new ones
        else
            result.TrySetException(new InvalidOperationException("The capture source kept changing size."));
    }, error => result.TrySetException(error)), error => result.TrySetException(error));

    void WhenConstraintsKnown(Action then, Action<Exception> fail)
    {
        if (IsStopped)
            fail(new InvalidOperationException("The capture source went away."));
        else if (constraintsKnown)
            then();
        else
            waiting.Add((then, fail));
    }

    /// <summary>Captures one frame; <paramref name="done"/> gets null if the buffer no longer matches the constraints.</summary>
    void CaptureFrame(Action<CapturedImage?> done, Action<Exception> fail)
    {
        if (size.Width <= 0 || size.Height <= 0)
        {
            fail(new InvalidOperationException("The capture source has no size."));
            return;
        }
        WlShmFormat format;
        if (formats.Contains(WlShmFormat.Argb8888))
            format = WlShmFormat.Argb8888;
        else if (formats.Contains(WlShmFormat.Xrgb8888))
            format = WlShmFormat.Xrgb8888;
        else
        {
            fail(new InvalidOperationException("The compositor offers no 32-bit shared-memory format for capture."));
            return;
        }
        if (pool is null || pool.BufferSize != size || bufferFormat != format)
        {
            buffer?.Dispose();
            pool?.Dispose();
            pool = new ShmPool(app.Shm, size, format);
            buffer = pool.CreateBuffer(pool.AddSlot());
            bufferFormat = format;
        }

        var frameSize = size;
        int version = constraintsVersion;
        var frame = session.CreateFrame();
        frame.Ready += () =>
        {
            int stride = frameSize.Width * 4;
            var pixels = new byte[stride * frameSize.Height];
            Marshal.Copy(pool.SlotPointer(0), pixels, 0, pixels.Length);
            frame.Dispose();
            done(new CapturedImage(frameSize.Width, frameSize.Height, stride, format, pixels));
        };
        frame.Failed += e =>
        {
            frame.Dispose();
            if (e.Reason == ExtImageCopyCaptureFrameV1FailureReason.BufferConstraints)
            {
                // New constraints may already have arrived; if not, wait for them.
                if (constraintsVersion == version)
                    constraintsKnown = false;
                done(null);
            }
            else
                fail(new InvalidOperationException($"Capture failed: {e.Reason}."));
        };
        frame.AttachBuffer(buffer!);
        frame.DamageBuffer(0, 0, frameSize.Width, frameSize.Height);
        frame.Capture();
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        session.Dispose();
        source.Dispose();
        buffer?.Dispose();
        pool?.Dispose();
    }
}
