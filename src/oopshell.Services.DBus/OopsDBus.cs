using Tmds.DBus.Protocol;

namespace oopshell.Services.DBus;

/// <summary>
/// Writes a message body. Takes the writer by <c>ref</c>: Tmds' <see cref="MessageWriter"/> is a struct, and a
/// copy (e.g. through <c>Action&lt;MessageWriter&gt;</c>) would silently drop everything written.
/// </summary>
public delegate void BodyWriter(ref MessageWriter writer);

/// <summary>Connecting to D-Bus from an oopshell app.</summary>
public static class OopsDBus
{
    /// <summary>
    /// Connects to the session bus (or <paramref name="address"/>). The connection is disposed with the app.
    /// Pass <c>app.SynchronizationContext</c> to signal watches so handlers run on the UI thread.
    /// </summary>
    public static async Task<DBusConnection> ConnectAsync(OopsApp app, string? address = null)
    {
        address ??= DBusAddress.Session ?? throw new InvalidOperationException("DBUS_SESSION_BUS_ADDRESS is not set.");
        var connection = new DBusConnection(address);
        await connection.ConnectAsync();
        app.Disposing += connection.Dispose;
        return connection;
    }

    /// <summary>Requests a well-known bus name; throws if another process owns it.</summary>
    public static async Task RequestNameOrThrowAsync(this DBusConnection connection, string name)
    {
        if (!await connection.TryRequestNameAsync(name, RequestNameOptions.Default))
            throw new InvalidOperationException($"The bus name {name} is already taken.");
    }

    /// <summary>Calls a method without arguments and returns the reply body reader result.</summary>
    public static Task<T> CallAsync<T>(this DBusConnection connection, string destination, string path, string iface, string member,
        MessageValueReader<T> readReply, BodyWriter? writeArgs = null, string? signature = null)
    {
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(destination, path, iface, member, signature, MessageFlags.None);
            writeArgs?.Invoke(ref writer);
            return connection.CallMethodAsync(writer.CreateMessage(), readReply);
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>Calls a method and waits for the (empty) reply.</summary>
    public static Task CallAsync(this DBusConnection connection, string destination, string path, string iface, string member,
        BodyWriter? writeArgs = null, string? signature = null)
    {
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(destination, path, iface, member, signature, MessageFlags.None);
            writeArgs?.Invoke(ref writer);
            return connection.CallMethodAsync(writer.CreateMessage());
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>Reads one property via <c>org.freedesktop.DBus.Properties.Get</c>.</summary>
    public static Task<VariantValue> GetPropertyAsync(this DBusConnection connection, string destination, string path, string iface, string property) =>
        connection.CallAsync(destination, path, "org.freedesktop.DBus.Properties", "Get",
            (message, _) => message.GetBodyReader().ReadVariantValue(),
            (ref MessageWriter w) =>
            {
                w.WriteString(iface);
                w.WriteString(property);
            }, "ss");

    /// <summary>Reads all properties of an interface via <c>org.freedesktop.DBus.Properties.GetAll</c>.</summary>
    public static Task<Dictionary<string, VariantValue>> GetAllPropertiesAsync(this DBusConnection connection, string destination, string path, string iface) =>
        connection.CallAsync(destination, path, "org.freedesktop.DBus.Properties", "GetAll",
            (message, _) => message.GetBodyReader().ReadDictionaryOfStringToVariantValue(),
            (ref MessageWriter w) => w.WriteString(iface), "s");

    /// <summary>
    /// Subscribes to a signal; <paramref name="handler"/> runs on the app's UI thread with the decoded value.
    /// Dispose the result to unsubscribe.
    /// </summary>
    public static async Task<IDisposable> WatchSignalAsync<T>(this DBusConnection connection, OopsApp app,
        string? sender, string? path, string iface, string member, MessageValueReader<T> reader, Action<T> handler)
    {
        return await connection.WatchSignalAsync(sender, path, iface, member, reader, (Notification<T> n) =>
        {
            if (n.HasValue)
                handler(n.Value);
        }, app.SynchronizationContext, ObserverFlags.None, null);
    }

    /// <summary>Emits a signal.</summary>
    public static void EmitSignal(this DBusConnection connection, string path, string iface, string member, string? signature = null, BodyWriter? writeArgs = null)
    {
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteSignalHeader(null, path, iface, member, signature);
            writeArgs?.Invoke(ref writer);
            if (!connection.TrySendMessage(writer.CreateMessage()))
                throw new InvalidOperationException($"Could not send signal {iface}.{member}: not connected.");
        }
        finally
        {
            writer.Dispose();
        }
    }
}

/// <summary>
/// An object exported on the bus whose method calls run on the oopshell UI thread, so handlers can touch
/// app state without locking. Answer every call with <see cref="MethodContext.Reply"/> or
/// <see cref="ReplyError"/>; unanswered calls get <c>org.freedesktop.DBus.Error.UnknownMethod</c>, unless the
/// handler called <see cref="Defer"/> to answer later (e.g. after the user typed a password).
/// </summary>
public abstract class ExportedObject(OopsApp app, string path) : IPathMethodHandler
{
    public string Path { get; } = path;

    public bool HandlesChildPaths => false;

    protected OopsApp App { get; } = app;

    async ValueTask IPathMethodHandler.HandleMethodAsync(MethodContext context)
    {
        await App.InvokeAsync(() =>
        {
            if (context.IsDBusIntrospectRequest)
                context.ReplyIntrospectXml([System.Text.Encoding.UTF8.GetBytes(IntrospectionXml)]);
            else
                HandleMethod(context);
            if (!context.ReplySent && !context.NoReplyExpected && !deferred.Remove(context))
                ReplyError(context, "org.freedesktop.DBus.Error.UnknownMethod", $"No such method {context.Request.InterfaceAsString}.{context.Request.MemberAsString}.");
            return Task.CompletedTask;
        });
    }

    readonly HashSet<MethodContext> deferred = new();

    /// <summary>
    /// Keeps <paramref name="context"/> open past <see cref="HandleMethod"/>: reply later with
    /// <see cref="ReplyDeferred"/> or <see cref="ReplyDeferredError"/> (on the UI thread).
    /// </summary>
    protected void Defer(MethodContext context)
    {
        context.DisposesAsynchronously = true;
        deferred.Add(context);
    }

    /// <summary>Answers a <see cref="Defer"/>red call with an empty reply and releases it.</summary>
    protected static void ReplyDeferred(MethodContext context)
    {
        ReplyEmpty(context);
        context.Dispose();
    }

    /// <summary>Answers a <see cref="Defer"/>red call with an error and releases it.</summary>
    protected static void ReplyDeferredError(MethodContext context, string errorName, string message)
    {
        ReplyError(context, errorName, message);
        context.Dispose();
    }

    /// <summary>Handles a call on the UI thread.</summary>
    protected abstract void HandleMethod(MethodContext context);

    /// <summary>The <c>&lt;interface&gt;</c> elements describing this object, for introspection.</summary>
    protected virtual string IntrospectionXml => "";

    // Not CreateReplyWriter + WriteError: that writer already holds a method-return header, and the message
    // with two headers made the bus drop the whole connection.
    protected static void ReplyError(MethodContext context, string errorName, string message) => context.ReplyError(errorName, message);

    protected static void ReplyEmpty(MethodContext context)
    {
        using var writer = context.CreateReplyWriter(null);
        context.Reply(writer.CreateMessage());
    }
}
