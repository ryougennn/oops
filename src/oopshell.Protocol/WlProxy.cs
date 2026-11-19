using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using oopshell.Native;
using static oopshell.Native.WaylandClient;

namespace oopshell.Protocol;

/// <summary>
/// Base class of every generated Wayland proxy. Owns the native <c>wl_proxy*</c> and a <see cref="GCHandle"/>
/// that native user data points at; both are released by <see cref="Dispose"/>.
/// </summary>
public abstract unsafe class WlProxy : IDisposable
{
    // Proxies we created carry this tag, so foreign proxies (EGL's, for example) are never mistaken for ours.
    static readonly byte** Tag = CreateTag();

    [ThreadStatic] static ExceptionDispatchInfo? pendingException;

    GCHandle self;
    readonly int ownerThread = Environment.CurrentManagedThreadId;

    protected WlProxy(nint handle, bool dispatch = true)
    {
        if (handle == 0)
            throw new ArgumentException("Null wl_proxy.", nameof(handle));
        Handle = handle;
        // libwayland reports 0 for unversioned proxies such as wl_display itself; protocol-wise that is version 1.
        BoundVersion = Math.Max(1u, wl_proxy_get_version(handle));
        if (!dispatch)
            return;
        self = GCHandle.Alloc(this);
        void* data = (void*)GCHandle.ToIntPtr(self);
        wl_proxy_set_tag(handle, Tag);
        if (wl_proxy_add_dispatcher(handle, &DispatchNative, data, data) != 0)
            throw new InvalidOperationException($"{Class}@{Id} already has a listener.");
    }

    /// <summary>The native <c>wl_proxy*</c>.</summary>
    public nint Handle { get; }

    /// <summary>The version this object was created or bound with.</summary>
    public uint BoundVersion { get; }

    /// <summary>The protocol object id.</summary>
    public uint Id => wl_proxy_get_id(Handle);

    /// <summary>The interface name, e.g. <c>wl_surface</c>.</summary>
    public string Class => ReadString(wl_proxy_get_class(Handle)) ?? "";

    public bool IsDisposed { get; private set; }

    public override string ToString() => IsDisposed ? $"{GetType().Name} (disposed)" : $"{Class}@{Id}";

    /// <summary>Sends the destructor request (if the interface has one) and destroys the proxy. Idempotent.</summary>
    public void Dispose()
    {
        if (IsDisposed)
            return;
        CheckThread();
        Destroy();
        Release();
    }

    /// <summary>Destroys the native proxy. Generated code overrides this to send the destructor request.</summary>
    protected virtual void Destroy() => wl_proxy_destroy(Handle);

    /// <summary>Handles one event. <paramref name="args"/> is only valid during the call.</summary>
    protected virtual void Dispatch(uint opcode, WlArgument* args) { }

    protected void Marshal(uint opcode, WlArgument* args, uint since, string request, bool destroy = false)
    {
        CheckUsable(since, request);
        wl_proxy_marshal_array_flags(Handle, opcode, null, BoundVersion, destroy ? MarshalFlagDestroy : 0, args);
        if (destroy)
            Release();
    }

    protected nint MarshalNew(uint opcode, WlArgument* args, WlInterface* iface, uint version, uint since, string request)
    {
        CheckUsable(since, request);
        nint created = wl_proxy_marshal_array_flags(Handle, opcode, iface, version, 0, args);
        if (created == 0)
            throw new OutOfMemoryException($"libwayland could not create the new object for {Class}.{request}.");
        return created;
    }

    void CheckUsable(uint since, string request)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        CheckThread();
        if (BoundVersion < since)
            throw new WaylandVersionException(Class, request, since, BoundVersion);
    }

    void Release()
    {
        IsDisposed = true;
        if (self.IsAllocated)
            self.Free();
    }

    [Conditional("DEBUG")]
    void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
            throw new InvalidOperationException($"{Class} was used from thread {Environment.CurrentManagedThreadId}; Wayland objects belong to the thread that created them.");
    }

    /// <summary>The managed proxy for a native one, or null if it is null or not created by oopshell.</summary>
    public static T? FromHandle<T>(nint handle) where T : WlProxy
    {
        if (handle == 0 || wl_proxy_get_tag(handle) != Tag)
            return null;
        return GCHandle.FromIntPtr((nint)wl_proxy_get_user_data(handle)).Target as T;
    }

    public static nint HandleOf(WlProxy? proxy) => proxy?.Handle ?? 0;

    public static string? ReadString(byte* s) => s == null ? null : System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)s);

    public static ReadOnlySpan<byte> ReadArray(WlArray* array) =>
        array == null ? default : new ReadOnlySpan<byte>(array->Data, (int)array->Size);

    protected static void CloseFd(int fd) => LibC.close(fd);

    /// <summary>Rethrows the first exception an event handler threw during the last dispatch, if any.</summary>
    /// <summary>
    /// Called with an exception an event handler threw (on the dispatching thread); return true to have it ignored,
    /// false to have it rethrown once dispatching ends. Set by the app (<c>OopsApp.UnhandledException</c>).
    /// </summary>
    public static Func<Exception, bool>? UnhandledException { get; set; }

    public static void ThrowPendingException()
    {
        var pending = pendingException;
        pendingException = null;
        pending?.Throw();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int DispatchNative(void* implementation, nint proxy, uint opcode, WlMessage* message, WlArgument* args)
    {
        // Exceptions must not unwind into libwayland: park them and rethrow on the loop thread.
        try
        {
            if (GCHandle.FromIntPtr((nint)implementation).Target is WlProxy target && !target.IsDisposed)
                target.Dispatch(opcode, args);
        }
        catch (Exception e) when (UnhandledException?.Invoke(e) != true)
        {
            pendingException ??= ExceptionDispatchInfo.Capture(e);
        }
        return 0;
    }

    static byte** CreateTag()
    {
        byte** slot = (byte**)NativeMemory.Alloc((nuint)sizeof(byte*));
        *slot = WlInterfaceTable.Utf8("oopshell");
        return slot;
    }
}

/// <summary>Implemented by every generated proxy; gives generic, reflection-free access to interface metadata.</summary>
public unsafe interface IWaylandProxy<TSelf> where TSelf : WlProxy, IWaylandProxy<TSelf>
{
    static abstract string InterfaceName { get; }
    /// <summary>The highest version these bindings know.</summary>
    static abstract uint MaxVersion { get; }
    static abstract WlInterface* NativeInterface { get; }
    /// <summary>Wraps a native proxy of this interface and starts dispatching its events.</summary>
    static abstract TSelf Wrap(nint handle);
}
