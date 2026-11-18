using System.Runtime.InteropServices;

namespace oopshell.Native;

/// <summary>P/Invoke surface of <c>libwayland-client.so.0</c>. Only non-varargs entry points.</summary>
public static unsafe partial class WaylandClient
{
    const string Lib = "libwayland-client.so.0";

    /// <summary><c>WL_MARSHAL_FLAG_DESTROY</c>: destroy the proxy after marshalling.</summary>
    public const uint MarshalFlagDestroy = 1;

    [LibraryImport(Lib)] public static partial nint wl_display_connect(byte* name);
    [LibraryImport(Lib)] public static partial nint wl_display_connect_to_fd(int fd);
    [LibraryImport(Lib)] public static partial void wl_display_disconnect(nint display);
    [LibraryImport(Lib)] public static partial int wl_display_get_fd(nint display);
    [LibraryImport(Lib, SetLastError = true)] public static partial int wl_display_roundtrip(nint display);
    [LibraryImport(Lib, SetLastError = true)] public static partial int wl_display_dispatch_pending(nint display);
    [LibraryImport(Lib)] public static partial int wl_display_prepare_read(nint display);
    [LibraryImport(Lib, SetLastError = true)] public static partial int wl_display_read_events(nint display);
    [LibraryImport(Lib)] public static partial void wl_display_cancel_read(nint display);
    [LibraryImport(Lib, SetLastError = true)] public static partial int wl_display_flush(nint display);
    [LibraryImport(Lib)] public static partial int wl_display_get_error(nint display);
    [LibraryImport(Lib)] public static partial uint wl_display_get_protocol_error(nint display, WlInterface** iface, uint* id);

    [LibraryImport(Lib)]
    public static partial nint wl_proxy_marshal_array_flags(nint proxy, uint opcode, WlInterface* iface, uint version, uint flags, WlArgument* args);
    [LibraryImport(Lib)]
    public static partial int wl_proxy_add_dispatcher(nint proxy, delegate* unmanaged[Cdecl]<void*, nint, uint, WlMessage*, WlArgument*, int> dispatcher, void* implementation, void* data);
    [LibraryImport(Lib)] public static partial void wl_proxy_destroy(nint proxy);
    [LibraryImport(Lib)] public static partial void* wl_proxy_get_user_data(nint proxy);
    [LibraryImport(Lib)] public static partial uint wl_proxy_get_version(nint proxy);
    [LibraryImport(Lib)] public static partial uint wl_proxy_get_id(nint proxy);
    [LibraryImport(Lib)] public static partial byte* wl_proxy_get_class(nint proxy);
    [LibraryImport(Lib)] public static partial void wl_proxy_set_tag(nint proxy, byte** tag);
    [LibraryImport(Lib)] public static partial byte** wl_proxy_get_tag(nint proxy);
}
