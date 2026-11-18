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
    readonly Surface surface;
    readonly WlShmFormat format;
    readonly int maxBuffers;
    readonly List<ShmBuffer> buffers = new();
    readonly DamageHistory damageHistory = new();
    ShmPool? pool;
    long frameCounter;
    bool starved;
    bool disposed;

    /// <param name="surface">The surface to present to.</param>
    /// <param name="format">A 32-bit <c>wl_shm</c> format.</param>
    /// <param name="maxBuffers">
    /// At least 2: compositors keep the current buffer until another one replaces it, so a single buffer
    /// could never be released.
    /// </param>
    public ShmSwapchain(Surface surface, WlShmFormat format = WlShmFormat.Argb8888, int maxBuffers = 3)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBuffers, 2);
        this.surface = surface;
        this.format = format;
        this.maxBuffers = maxBuffers;
    }

    public WlShmFormat Format => format;

    /// <summary>
    /// A buffer of <see cref="RenderFrame.BufferSize"/> to draw into, or null when all buffers are still in use
    /// by the compositor. In that case skip the frame: the surface is invalidated again when one is released.
    /// </summary>
    public ShmBuffer? Acquire(RenderFrame frame) => Acquire(frame.BufferSize, frame.Damage);

    /// <summary>Like <see cref="Acquire(RenderFrame)"/>, for presenting outside a render handler.</summary>
    public ShmBuffer? Acquire(Size size, Rectangle damage)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (size.Width <= 0 || size.Height <= 0)
            throw new ArgumentException("Buffer size must be positive.", nameof(size));

        if (pool is null || pool.BufferSize != size)
            Resize(size);

        ShmBuffer? buffer = null;
        foreach (var candidate in buffers)
        {
            if (!candidate.IsBusy && (buffer is null || candidate.LastPresentedFrame > buffer.LastPresentedFrame))
                buffer = candidate; // the most recently presented free buffer has the youngest contents
        }
        if (buffer is null)
        {
            if (buffers.Count >= maxBuffers)
            {
                starved = true;
                return null;
            }
            buffer = new ShmBuffer(this, pool!, pool!.AddSlot(), size);
            buffers.Add(buffer);
        }
        buffer.Lease(frameCounter, damageHistory.RepairArea(buffer.AgeAt(frameCounter), damage, size));
        return buffer;
    }

    internal void OnPresented(ShmBuffer buffer, Rectangle damage)
    {
        frameCounter++;
        damageHistory.Add(damage);
        buffer.LastPresentedFrame = frameCounter;
    }

    internal void OnReleased(ShmBuffer buffer)
    {
        if (buffer.Pool != pool)
        {
            buffers.Remove(buffer);
            buffer.Destroy();
        }
        if (starved && !disposed)
        {
            starved = false;
            surface.Invalidate();
        }
    }

    internal Surface Surface => surface;

    void Resize(Size size)
    {
        // Buffers of the old size die as soon as the compositor lets go of them; their pool can go right away
        // (wl_shm_pool.destroy keeps existing buffers valid).
        foreach (var buffer in buffers.Where(b => !b.IsBusy).ToList())
        {
            buffers.Remove(buffer);
            buffer.Destroy();
        }
        pool?.Dispose();
        pool = new ShmPool(surface.App.Shm, size, format);
        damageHistory.Clear();
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        foreach (var buffer in buffers)
            buffer.Destroy();
        buffers.Clear();
        pool?.Dispose();
    }
}

/// <summary>A buffer leased from a <see cref="ShmSwapchain"/> for one frame. Present it, or dispose it to give it back.</summary>
public sealed unsafe class ShmBuffer : IDisposable
{
    readonly ShmSwapchain chain;
    readonly int slot;
    readonly WlBuffer proxy;
    bool leased;

    internal ShmBuffer(ShmSwapchain chain, ShmPool pool, int slot, Size size)
    {
        this.chain = chain;
        this.slot = slot;
        Pool = pool;
        Size = size;
        proxy = pool.CreateBuffer(slot);
        proxy.Release += () =>
        {
            IsBusy = false;
            chain.OnReleased(this);
        };
    }

    internal ShmPool Pool { get; }

    /// <summary>True from lease until the compositor releases it.</summary>
    internal bool IsBusy { get; private set; }

    internal long LastPresentedFrame { get; set; }

    long leaseFrame;

    public Size Size { get; }

    /// <summary>Bytes per row.</summary>
    public int Stride => Size.Width * 4;

    /// <summary>Start of the pixel memory.</summary>
    public nint Pointer => Pool.SlotPointer(slot);

    /// <summary>Pixels, row-major, <see cref="Stride"/>/4 per row. For ARGB8888 each is premultiplied 0xAARRGGBB.</summary>
    public Span<uint> Pixels => new((void*)Pointer, Size.Width * Size.Height);

    /// <summary>0 = contents undefined; N = contents are what was presented N frames ago.</summary>
    public int Age => AgeAt(leaseFrame);

    /// <summary>The area you must redraw for the buffer to be fully up to date (this frame's damage plus <see cref="Age"/> history).</summary>
    public Rectangle Damage { get; private set; }

    internal int AgeAt(long currentFrame) => LastPresentedFrame == 0 ? 0 : (int)(currentFrame + 1 - LastPresentedFrame);

    internal void Lease(long frame, Rectangle damage)
    {
        leased = true;
        IsBusy = true;
        leaseFrame = frame;
        Damage = damage;
    }

    /// <summary>Attaches the buffer, damages <paramref name="damage"/> (buffer pixels) and commits.</summary>
    public void Present(Rectangle damage)
    {
        if (!leased)
            throw new InvalidOperationException("This buffer was already presented or returned.");
        leased = false;
        var surface = chain.Surface;
        surface.PrepareCommit();
        var wl = surface.Proxy;
        wl.Attach(proxy, 0, 0);
        if (wl.BoundVersion >= 4)
            wl.DamageBuffer(damage.X, damage.Y, damage.Width, damage.Height);
        else
            wl.Damage(0, 0, int.MaxValue, int.MaxValue); // shortcut: pre-v4 surfaces get full damage
        wl.Commit();
        chain.OnPresented(this, damage);
    }

    /// <summary>Presents with full damage.</summary>
    public void Present() => Present(new Rectangle(Point.Empty, Size));

    /// <summary>Returns the buffer unpresented, if it was not presented. Safe to call after <see cref="Present(Rectangle)"/>.</summary>
    public void Dispose()
    {
        if (!leased)
            return;
        leased = false;
        IsBusy = false;
    }

    internal void Destroy() => proxy.Dispose();
}

/// <summary>A memfd-backed <c>wl_shm_pool</c> of equally sized slots that grows one slot at a time.</summary>
internal sealed unsafe class ShmPool : IDisposable
{
    readonly int fd;
    readonly WlShmPool pool;
    readonly WlShmFormat format;
    readonly int slotBytes;
    int slots;
    nint mapping;
    long mappedBytes;

    public ShmPool(WlShm shm, Size bufferSize, WlShmFormat format)
    {
        BufferSize = bufferSize;
        this.format = format;
        slotBytes = bufferSize.Width * bufferSize.Height * 4;
        using (var name = new NativeUtf8("oopshell-shm"))
            fd = LibC.memfd_create(name.Pointer, LibC.MFD_CLOEXEC | LibC.MFD_ALLOW_SEALING);
        if (fd < 0)
            throw LibC.LastError("memfd_create");
        Grow(1);
        pool = shm.CreatePool(fd, slotBytes);
    }

    public Size BufferSize { get; }

    public int AddSlot()
    {
        if (slots * (long)slotBytes >= mappedBytes)
        {
            Grow(slots + 1);
            pool.Resize(checked((int)mappedBytes));
        }
        return slots++;
    }

    public WlBuffer CreateBuffer(int slot) =>
        pool.CreateBuffer(slot * slotBytes, BufferSize.Width, BufferSize.Height, BufferSize.Width * 4, format);

    public nint SlotPointer(int slot) => mapping + slot * slotBytes;

    void Grow(int slotCount)
    {
        long bytes = (long)slotCount * slotBytes;
        if (LibC.ftruncate(fd, bytes) < 0)
            throw LibC.LastError("ftruncate");
        if (mapping != 0)
            LibC.munmap(mapping, (nuint)mappedBytes);
        mapping = LibC.mmap(0, (nuint)bytes, LibC.PROT_READ | LibC.PROT_WRITE, LibC.MAP_SHARED, fd, 0);
        if (mapping == LibC.MAP_FAILED)
            throw LibC.LastError("mmap");
        mappedBytes = bytes;
    }

    public void Dispose()
    {
        pool.Dispose();
        LibC.munmap(mapping, (nuint)mappedBytes);
        LibC.close(fd);
    }
}
