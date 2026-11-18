using System.Runtime.InteropServices;
using oopshell.Native;
using static oopshell.Native.WaylandClient;

namespace oopshell.Protocol;

/// <summary>The connection to the compositor. Disposing it disconnects.</summary>
public sealed unsafe partial class WlDisplay
{
    /// <summary>Connects to <paramref name="name"/>, or to <c>WAYLAND_SOCKET</c>/<c>WAYLAND_DISPLAY</c> when null.</summary>
    public static WlDisplay Connect(string? name = null)
    {
        using var nameUtf8 = new NativeUtf8(name);
        nint display = wl_display_connect(nameUtf8.Pointer);
        if (display == 0)
        {
            string target = name ?? Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") ?? "wayland-0";
            throw new WaylandConnectionException($"Could not connect to Wayland display '{target}': {LibC.ErrorText(System.Runtime.InteropServices.Marshal.GetLastSystemError())}.");
        }
        return Wrap(display);
    }

    protected override void Destroy() => wl_display_disconnect(Handle);

    /// <summary>The connection's file descriptor, for polling.</summary>
    public int Fd => wl_display_get_fd(Handle);

    /// <summary>Blocks until the compositor has processed every request sent so far, dispatching events meanwhile.</summary>
    public void Roundtrip()
    {
        int result = wl_display_roundtrip(Handle);
        WlProxy.ThrowPendingException();
        if (result < 0)
            ThrowError();
    }

    /// <summary>Dispatches already-read events without blocking. Returns how many were dispatched.</summary>
    public int DispatchPending()
    {
        int result = wl_display_dispatch_pending(Handle);
        WlProxy.ThrowPendingException();
        if (result < 0)
            ThrowError();
        return result;
    }

    /// <summary>Announces an intent to read; false means events are queued and must be dispatched first.</summary>
    public bool PrepareRead() => wl_display_prepare_read(Handle) == 0;

    public void CancelRead() => wl_display_cancel_read(Handle);

    /// <summary>Reads events from the socket after <see cref="PrepareRead"/>. Does not dispatch them.</summary>
    public void ReadEvents()
    {
        if (wl_display_read_events(Handle) < 0)
            ThrowError();
    }

    /// <summary>Sends buffered requests. Returns false if the socket is full (wait for it to become writable, then retry).</summary>
    public bool Flush()
    {
        if (wl_display_flush(Handle) >= 0)
            return true;
        if (System.Runtime.InteropServices.Marshal.GetLastPInvokeError() == LibC.EAGAIN)
            return false;
        ThrowError();
        return false;
    }

    /// <summary>Throws the connection's fatal error, if it has one.</summary>
    public void ThrowIfError()
    {
        if (wl_display_get_error(Handle) != 0)
            ThrowError();
    }

    void ThrowError()
    {
        int error = wl_display_get_error(Handle);
        if (error == 0)
            error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        WlInterface* iface;
        uint id;
        uint code = wl_display_get_protocol_error(Handle, &iface, &id);
        if (iface != null)
        {
            string name = WlProxy.ReadString(iface->Name) ?? "?";
            throw new WaylandProtocolException(name, id, code, WlInterfaceTable.ErrorName(name, code));
        }
        throw new WaylandConnectionException($"Wayland connection lost: {LibC.ErrorText(error)}.");
    }
}
