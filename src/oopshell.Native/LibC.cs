using System.Runtime.InteropServices;

namespace oopshell.Native;

/// <summary>The handful of libc calls oopshell needs. All set errno (read it with <see cref="Marshal.GetLastPInvokeError"/>).</summary>
public static unsafe partial class LibC
{
    const string Lib = "libc";

    public const int EINTR = 4;
    public const int EAGAIN = 11;

    public const int O_CLOEXEC = 0x80000;
    public const int O_NONBLOCK = 0x800;

    public const int EPOLL_CLOEXEC = O_CLOEXEC;
    public const int EPOLL_CTL_ADD = 1;
    public const int EPOLL_CTL_DEL = 2;
    public const int EPOLL_CTL_MOD = 3;
    public const uint EPOLLIN = 0x1;
    public const uint EPOLLOUT = 0x4;
    public const uint EPOLLERR = 0x8;
    public const uint EPOLLHUP = 0x10;

    public const int EFD_CLOEXEC = O_CLOEXEC;
    public const int EFD_NONBLOCK = O_NONBLOCK;

    public const int CLOCK_REALTIME = 0;
    public const int CLOCK_MONOTONIC = 1;
    public const int TFD_TIMER_ABSTIME = 1;
    public const int TFD_TIMER_CANCEL_ON_SET = 2;
    public const int ECANCELED = 125;
    public const int TFD_CLOEXEC = O_CLOEXEC;
    public const int TFD_NONBLOCK = O_NONBLOCK;

    public const uint MFD_CLOEXEC = 1;
    public const uint MFD_ALLOW_SEALING = 2;
    public const int PROT_READ = 1;
    public const int PROT_WRITE = 2;
    public const int MAP_SHARED = 1;
    public const int MAP_PRIVATE = 2;
    public static readonly nint MAP_FAILED = -1;

    [LibraryImport(Lib, SetLastError = true)] public static partial int epoll_create1(int flags);
    [LibraryImport(Lib, SetLastError = true)] private static partial int epoll_ctl(int epfd, int op, int fd, byte* ev);
    [LibraryImport(Lib, SetLastError = true)] private static partial int epoll_wait(int epfd, byte* events, int maxevents, int timeout);

    [LibraryImport(Lib, SetLastError = true)] public static partial int eventfd(uint initval, int flags);
    [LibraryImport(Lib, SetLastError = true)] public static partial int timerfd_create(int clockid, int flags);
    [LibraryImport(Lib, SetLastError = true)] public static partial int timerfd_settime(int fd, int flags, ITimerSpec* newValue, ITimerSpec* oldValue);
    [LibraryImport(Lib, SetLastError = true)] public static partial int memfd_create(byte* name, uint flags);
    [LibraryImport(Lib, SetLastError = true)] public static partial int ftruncate(int fd, long length);
    [LibraryImport(Lib, SetLastError = true)] public static partial nint mmap(nint addr, nuint length, int prot, int flags, int fd, long offset);
    [LibraryImport(Lib, SetLastError = true)] public static partial int munmap(nint addr, nuint length);
    [LibraryImport(Lib, SetLastError = true)] public static partial nint read(int fd, void* buf, nuint count);
    [LibraryImport(Lib, SetLastError = true)] public static partial nint write(int fd, void* buf, nuint count);
    [LibraryImport(Lib, SetLastError = true)] public static partial int close(int fd);
    [LibraryImport(Lib, SetLastError = true)] public static partial int dup(int fd);
    [LibraryImport(Lib, SetLastError = true)] public static partial int pipe2(int* fds, int flags);

    // struct epoll_event is packed on x86-64 (12 bytes) but naturally aligned elsewhere (16 bytes).
    static readonly bool EpollPacked = RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.X86;
    static int EpollEventSize => EpollPacked ? 12 : 16;
    static int EpollDataOffset => EpollPacked ? 4 : 8;

    public static int EpollCtl(int epfd, int op, int fd, uint events, ulong data)
    {
        byte* ev = stackalloc byte[16];
        new Span<byte>(ev, 16).Clear();
        *(uint*)ev = events;
        *(ulong*)(ev + EpollDataOffset) = data;
        return epoll_ctl(epfd, op, fd, ev);
    }

    /// <summary>Waits for events and writes them to <paramref name="events"/>. Returns the count, or -1 with errno set.</summary>
    public static int EpollWait(int epfd, Span<EpollEvent> events, int timeoutMs)
    {
        int size = EpollEventSize;
        byte* buffer = stackalloc byte[size * events.Length];
        int n = epoll_wait(epfd, buffer, events.Length, timeoutMs);
        for (int i = 0; i < n; i++)
        {
            byte* ev = buffer + i * size;
            events[i] = new EpollEvent(*(uint*)ev, *(ulong*)(ev + EpollDataOffset));
        }
        return n;
    }

    public static string ErrorText(int errno) => Marshal.GetPInvokeErrorMessage(errno);

    /// <summary>Throws an <see cref="IOException"/> describing the last errno.</summary>
    public static IOException LastError(string what)
    {
        int errno = Marshal.GetLastPInvokeError();
        return new IOException($"{what} failed: {ErrorText(errno)} (errno {errno})");
    }
}

/// <summary>An epoll event decoded into a portable shape.</summary>
public readonly record struct EpollEvent(uint Events, ulong Data);

[StructLayout(LayoutKind.Sequential)]
public struct TimeSpec
{
    public long Seconds;
    public long Nanoseconds;

    public static TimeSpec From(TimeSpan span) => new()
    {
        Seconds = span.Ticks / TimeSpan.TicksPerSecond,
        Nanoseconds = span.Ticks % TimeSpan.TicksPerSecond * 100,
    };
}

[StructLayout(LayoutKind.Sequential)]
public struct ITimerSpec
{
    public TimeSpec Interval;
    public TimeSpec Value;
}
