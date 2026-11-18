using System.Runtime.InteropServices;

namespace oopshell.Native;

/// <summary>Mirror of libwayland's <c>union wl_argument</c>.</summary>
[StructLayout(LayoutKind.Explicit, Size = 8)]
public unsafe struct WlArgument
{
    [FieldOffset(0)] public int i;
    [FieldOffset(0)] public uint u;
    /// <summary>24.8 fixed point.</summary>
    [FieldOffset(0)] public int f;
    [FieldOffset(0)] public byte* s;
    /// <summary>A <c>wl_object*</c> (which is the <c>wl_proxy*</c> on the client side).</summary>
    [FieldOffset(0)] public nint o;
    [FieldOffset(0)] public uint n;
    [FieldOffset(0)] public WlArray* a;
    [FieldOffset(0)] public int h;
}

/// <summary>Mirror of <c>struct wl_array</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct WlArray
{
    public nuint Size;
    public nuint Alloc;
    public void* Data;
}

/// <summary>Mirror of <c>struct wl_message</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct WlMessage
{
    public byte* Name;
    public byte* Signature;
    public WlInterface** Types;
}

/// <summary>Mirror of <c>struct wl_interface</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct WlInterface
{
    public byte* Name;
    public int Version;
    public int MethodCount;
    public WlMessage* Methods;
    public int EventCount;
    public WlMessage* Events;
}
