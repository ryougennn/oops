using System.Drawing;
using oopshell.Native;
using oopshell.Protocol;

namespace oopshell;

/// <summary>
/// CPU rendering into shared memory (<c>wl_shm</c>). Keeps up to <c>maxBuffers</c> buffers, reuses the ones the
/// compositor released, and tracks buffer age so renderers can redraw only what changed.
/// Supports 32-bit formats (ARGB/XRGB/ABGR/XBGR 8888).
/// </summary>
public sealed class ShmSwapchain : IDisposable
{
    private readonly Surface _surface;
    private readonly WlShmFormat _format;
    private readonly int _maxBuffers;
    private readonly List<ShmBuffer> _buffers = new();
    private readonly DamageHistory _damageHistory = new();
    private ShmPool? _pool;
    private long _frameCounter;
    private bool _starved;
    private bool _disposed;

    /// <param name="surface">The surface to present to.</param>
    /// <param name="format">A 32-bit <c>wl_shm</c> format.</param>
    /// <param name="maxBuffers">
    /// At least 2: compositors keep the current buffer until another one replaces it, so a single buffer
    /// could never be released.
    /// </param>
    public ShmSwapchain(Surface surface, WlShmFormat format = WlShmFormat.Argb8888, int maxBuffers = 3)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBuffers, 2);
        _surface = surface;
        _format = format;
        _maxBuffers = maxBuffers;
    }

    public WlShmFormat Format => _format;

    /// <summary>
    /// A buffer of <see cref="RenderFrame.BufferSize"/> to draw into, or null when all buffers are still in use
    /// by the compositor. In that case skip the frame: the surface is invalidated again when one is released.
    /// </summary>
    public ShmBuffer? Acquire(RenderFrame frame) => Acquire(frame.BufferSize, frame.Damage);

    /// <summary>Like <see cref="Acquire(RenderFrame)"/>, for presenting outside a render handler.</summary>
    public ShmBuffer? Acquire(Size size, Rectangle damage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (size.Width <= 0 || size.Height <= 0)
            throw new ArgumentException("Buffer size must be positive.", nameof(size));

        if (_pool is null || _pool.BufferSize != size)
            Resize(size);

        ShmBuffer? buffer = null;
        foreach (var candidate in _buffers)
        {
            if (!candidate.IsBusy && (buffer is null || candidate.LastPresentedFrame > buffer.LastPresentedFrame))
                buffer = candidate; // the most recently presented free buffer has the youngest contents
        }
        if (buffer is null)
        {
            if (_buffers.Count >= _maxBuffers)
            {
                _starved = true;
                return null;
            }
            buffer = new ShmBuffer(this, _pool!, _pool!.AddSlot(), size);
            _buffers.Add(buffer);
        }
        buffer.Lease(_frameCounter, _damageHistory.RepairArea(buffer.AgeAt(_frameCounter), damage, size));
        return buffer;
    }

    internal void OnPresented(ShmBuffer buffer, Rectangle damage)
    {
        _frameCounter++;
        _damageHistory.Add(damage);
        buffer.LastPresentedFrame = _frameCounter;
    }

    internal void OnReleased(ShmBuffer buffer)
    {
        if (buffer.Pool != _pool)
        {
            _buffers.Remove(buffer);
            buffer.Destroy();
        }
        if (_starved && !_disposed)
        {
            _starved = false;
            _surface.Invalidate();
        }
    }

    internal Surface Surface => _surface;

    private void Resize(Size size)
    {
        // Buffers of the old size die as soon as the compositor lets go of them; their pool can go right away
        // (wl_shm_pool.destroy keeps existing buffers valid).
        foreach (var buffer in _buffers.Where(b => !b.IsBusy).ToList())
        {
            _buffers.Remove(buffer);
            buffer.Destroy();
        }
        _pool?.Dispose();
        _pool = new ShmPool(_surface.App.Shm, size, _format);
        _damageHistory.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var buffer in _buffers)
            buffer.Destroy();
        _buffers.Clear();
        _pool?.Dispose();
    }
}

/// <summary>A buffer leased from a <see cref="ShmSwapchain"/> for one frame. Present it, or dispose it to give it back.</summary>
public sealed unsafe class ShmBuffer : IDisposable
{
    private readonly ShmSwapchain _chain;
    private readonly int _slot;
    private readonly WlBuffer _proxy;
    private bool _leased;

    internal ShmBuffer(ShmSwapchain chain, ShmPool pool, int slot, Size size)
    {
        _chain = chain;
        _slot = slot;
        Pool = pool;
        Size = size;
        _proxy = pool.CreateBuffer(slot);
        _proxy.Release += () =>
        {
            IsBusy = false;
            chain.OnReleased(this);
        };
    }

    internal ShmPool Pool { get; }

    /// <summary>True from lease until the compositor releases it.</summary>
    internal bool IsBusy { get; private set; }

    internal long LastPresentedFrame { get; set; }

    private long _leaseFrame;

    public Size Size { get; }

    /// <summary>Bytes per row.</summary>
    public int Stride => Size.Width * 4;

    /// <summary>Start of the pixel memory.</summary>
    public nint Pointer => Pool.SlotPointer(_slot);

    /// <summary>Pixels, row-major, <see cref="Stride"/>/4 per row. For ARGB8888 each is premultiplied 0xAARRGGBB.</summary>
    public Span<uint> Pixels => new((void*)Pointer, Size.Width * Size.Height);

    /// <summary>0 = contents undefined; N = contents are what was presented N frames ago.</summary>
    public int Age => AgeAt(_leaseFrame);

    /// <summary>The area you must redraw for the buffer to be fully up to date (this frame's damage plus <see cref="Age"/> history).</summary>
    public Rectangle Damage { get; private set; }

    internal int AgeAt(long currentFrame) => LastPresentedFrame == 0 ? 0 : (int)(currentFrame + 1 - LastPresentedFrame);

    internal void Lease(long frame, Rectangle damage)
    {
        _leased = true;
        IsBusy = true;
        _leaseFrame = frame;
        Damage = damage;
    }

    /// <summary>Attaches the buffer, damages <paramref name="damage"/> (buffer pixels) and commits.</summary>
    public void Present(Rectangle damage)
    {
        if (!_leased)
            throw new InvalidOperationException("This buffer was already presented or returned.");
        _leased = false;
        var surface = _chain.Surface;
        var flash = surface.App.ReloadFlashRect(Size, surface.Scale);
        if (!flash.IsEmpty)
        {
            for (int y = flash.Top; y < flash.Bottom; y++)
                Pixels.Slice(y * Size.Width + flash.Left, flash.Width).Fill(0xFFFF00FF);
            damage = Rectangle.Union(damage, flash);
        }
        surface.PrepareCommit();
        var wl = surface.Proxy;
        wl.Attach(_proxy, 0, 0);
        if (wl.BoundVersion >= 4)
            wl.DamageBuffer(damage.X, damage.Y, damage.Width, damage.Height);
        else
            wl.Damage(0, 0, int.MaxValue, int.MaxValue); // shortcut: pre-v4 surfaces get full damage
        wl.Commit();
        _chain.OnPresented(this, damage);
    }

    /// <summary>Presents with full damage.</summary>
    public void Present() => Present(new Rectangle(Point.Empty, Size));

    /// <summary>Returns the buffer unpresented, if it was not presented. Safe to call after <see cref="Present(Rectangle)"/>.</summary>
    public void Dispose()
    {
        if (!_leased)
            return;
        _leased = false;
        IsBusy = false;
    }

    internal void Destroy() => _proxy.Dispose();
}

/// <summary>A memfd-backed <c>wl_shm_pool</c> of equally sized slots that grows one slot at a time.</summary>
internal sealed unsafe class ShmPool : IDisposable
{
    private readonly int _fd;
    private readonly WlShmPool _pool;
    private readonly WlShmFormat _format;
    private readonly int _slotBytes;
    private int _slots;
    private nint _mapping;
    private long _mappedBytes;

    public ShmPool(WlShm shm, Size bufferSize, WlShmFormat format)
    {
        BufferSize = bufferSize;
        _format = format;
        _slotBytes = SlotBytes(bufferSize);
        using (var name = new NativeUtf8("oopshell-shm"))
            _fd = LibC.memfd_create(name.Pointer, LibC.MFD_CLOEXEC | LibC.MFD_ALLOW_SEALING);
        if (_fd < 0)
            throw LibC.LastError("memfd_create");
        Grow(1);
        _pool = shm.CreatePool(_fd, _slotBytes);
    }

    public Size BufferSize { get; }

    /// <summary>Largest buffer side accepted: sizes come from the compositor, and w*h*4 must not overflow.</summary>
    public const int MaxSide = 16384;

    /// <summary>Bytes for one buffer of <paramref name="size"/>; throws for empty or oversized buffers instead of wrapping.</summary>
    public static int SlotBytes(Size size) =>
        size.Width is > 0 and <= MaxSide && size.Height is > 0 and <= MaxSide
            ? size.Width * size.Height * 4
            : throw new ArgumentOutOfRangeException(nameof(size), $"Buffer size {size.Width}x{size.Height} is outside 1..{MaxSide}.");

    public int AddSlot()
    {
        if (_slots * (long)_slotBytes >= _mappedBytes)
        {
            Grow(_slots + 1);
            _pool.Resize(checked((int)_mappedBytes));
        }
        return _slots++;
    }

    public WlBuffer CreateBuffer(int slot) =>
        _pool.CreateBuffer(slot * _slotBytes, BufferSize.Width, BufferSize.Height, BufferSize.Width * 4, _format);

    public nint SlotPointer(int slot) => _mapping + slot * _slotBytes;

    private void Grow(int slotCount)
    {
        long bytes = (long)slotCount * _slotBytes;
        if (LibC.ftruncate(_fd, bytes) < 0)
            throw LibC.LastError("ftruncate");
        if (_mapping != 0)
            LibC.munmap(_mapping, (nuint)_mappedBytes);
        _mapping = LibC.mmap(0, (nuint)bytes, LibC.PROT_READ | LibC.PROT_WRITE, LibC.MAP_SHARED, _fd, 0);
        if (_mapping == LibC.MAP_FAILED)
            throw LibC.LastError("mmap");
        _mappedBytes = bytes;
    }

    public void Dispose()
    {
        _pool.Dispose();
        LibC.munmap(_mapping, (nuint)_mappedBytes);
        LibC.close(_fd);
    }
}
