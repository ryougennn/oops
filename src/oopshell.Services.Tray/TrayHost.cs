using System.Collections.ObjectModel;
using oopshell.Services.DBus;
using Tmds.DBus.Protocol;

namespace oopshell.Services.Tray;

/// <summary>
/// A StatusNotifierItem host: the list of tray items. If no <c>org.kde.StatusNotifierWatcher</c> runs, this
/// process becomes the watcher too; otherwise it registers with the existing one (e.g. another bar's).
/// </summary>
public sealed class TrayHost : IDisposable
{
    public const string WatcherName = "org.kde.StatusNotifierWatcher";
    public const string WatcherPath = "/StatusNotifierWatcher";

    readonly OopsApp app;
    readonly DBusConnection connection;
    readonly ObservableCollection<TrayItem> items = new();
    readonly List<IDisposable> watches = new();
    StatusNotifierWatcher? watcher;

    TrayHost(OopsApp app, DBusConnection connection)
    {
        this.app = app;
        this.connection = connection;
        Items = new ReadOnlyObservableCollection<TrayItem>(items);
    }

    public static async Task<TrayHost> StartAsync(OopsApp app, DBusConnection connection)
    {
        var host = new TrayHost(app, connection);
        await host.InitializeAsync();
        return host;
    }

    /// <summary>Current tray items, in registration order.</summary>
    public ReadOnlyObservableCollection<TrayItem> Items { get; }

    /// <summary>True if this process is the StatusNotifierWatcher.</summary>
    public bool IsWatcher => watcher is not null;

    async Task InitializeAsync()
    {
        string hostName = $"org.kde.StatusNotifierHost-{Environment.ProcessId}";
        await connection.TryRequestNameAsync(hostName, RequestNameOptions.Default);

        var candidate = new StatusNotifierWatcher(app, connection);
        connection.AddMethodHandler(candidate);
        if (await connection.TryRequestNameAsync(WatcherName, RequestNameOptions.Default))
        {
            watcher = candidate;
            watcher.ItemRegistered += service => _ = AddAsync(service);
            watcher.ItemUnregistered += Remove;
            watcher.RegisterHost(hostName);
            return;
        }

        // Someone else is the watcher: follow its signals.
        connection.RemoveMethodHandler(WatcherPath);
        watches.Add(await connection.WatchSignalAsync(app, WatcherName, WatcherPath, WatcherName, "StatusNotifierItemRegistered",
            (message, _) => message.GetBodyReader().ReadString(), service => _ = AddAsync(service)));
        watches.Add(await connection.WatchSignalAsync(app, WatcherName, WatcherPath, WatcherName, "StatusNotifierItemUnregistered",
            (message, _) => message.GetBodyReader().ReadString(), Remove));
        await connection.CallAsync(WatcherName, WatcherPath, WatcherName, "RegisterStatusNotifierHost", (ref MessageWriter w) => w.WriteString(hostName), "s");
        var registered = await connection.GetPropertyAsync(WatcherName, WatcherPath, WatcherName, "RegisteredStatusNotifierItems");
        await Task.WhenAll((registered.AsStrings() ?? []).Select(AddAsync));
    }

    /// <summary>How long an item may take to answer before it is left out.</summary>
    static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(3);

    async Task AddAsync(string service)
    {
        if (items.Any(i => i.Service == service))
            return;
        var item = new TrayItem(app, connection, service);
        try
        {
            await item.StartAsync().WaitAsync(PeerTimeout);
        }
        catch (Exception)
        {
            return; // registered but not answering, or answering nonsense: one bad item must not stop the tray
        }
        await app.InvokeAsync(() =>
        {
            if (!items.Any(i => i.Service == service))
                items.Add(item);
            return Task.CompletedTask;
        });
    }

    void Remove(string service)
    {
        var item = items.FirstOrDefault(i => i.Service == service);
        if (item is null)
            return;
        item.Stop();
        items.Remove(item);
    }

    public void Dispose()
    {
        foreach (var watch in watches)
            watch.Dispose();
        foreach (var item in items)
            item.Stop();
        watcher?.Dispose();
    }
}

/// <summary>The <c>org.kde.StatusNotifierWatcher</c> service: the registry apps announce their tray items to.</summary>
internal sealed class StatusNotifierWatcher(OopsApp app, DBusConnection connection) : ExportedObject(app, TrayHost.WatcherPath), IDisposable
{
    readonly List<string> items = new();
    readonly List<string> hosts = new();
    IDisposable? nameWatch;

    public event Action<string>? ItemRegistered;
    public event Action<string>? ItemUnregistered;

    public void RegisterHost(string name)
    {
        if (!hosts.Contains(name))
            hosts.Add(name);
        connection.EmitSignal(TrayHost.WatcherPath, TrayHost.WatcherName, "StatusNotifierHostRegistered");
    }

    protected override void HandleMethod(MethodContext context)
    {
        var request = context.Request;
        switch ((request.InterfaceAsString, request.MemberAsString))
        {
            case (TrayHost.WatcherName, "RegisterStatusNotifierItem"):
            {
                string arg = request.GetBodyReader().ReadString();
                string sender = request.SenderAsString ?? "";
                // Apps pass either their bus name or (Ayatana style) just an object path.
                string service = arg.StartsWith('/') ? sender + arg : arg;
                // A unique name must be the caller's own; anything registered lives only as long as its caller.
                if (!arg.StartsWith(':') || arg == sender || arg.StartsWith(sender + "/"))
                    RegisterItem(service, sender);
                ReplyEmpty(context);
                break;
            }
            case (TrayHost.WatcherName, "RegisterStatusNotifierHost"):
                RegisterHost(request.GetBodyReader().ReadString());
                ReplyEmpty(context);
                break;
            case ("org.freedesktop.DBus.Properties", "Get"):
            {
                var reader = request.GetBodyReader();
                reader.ReadString();
                string property = reader.ReadString();
                var writer = context.CreateReplyWriter("v");
                try
                {
                    WriteProperty(ref writer, property, items, hosts);
                    context.Reply(writer.CreateMessage());
                }
                finally
                {
                    writer.Dispose();
                }
                break;
            }
            case ("org.freedesktop.DBus.Properties", "GetAll"):
            {
                var writer = context.CreateReplyWriter("a{sv}");
                try
                {
                    var dict = writer.WriteDictionaryStart();
                    foreach (var name in new[] { "RegisteredStatusNotifierItems", "IsStatusNotifierHostRegistered", "ProtocolVersion" })
                    {
                        writer.WriteDictionaryEntryStart();
                        writer.WriteString(name);
                        WriteProperty(ref writer, name, items, hosts);
                    }
                    writer.WriteDictionaryEnd(dict);
                    context.Reply(writer.CreateMessage());
                }
                finally
                {
                    writer.Dispose();
                }
                break;
            }
        }
    }

    // By ref: MessageWriter is a struct, and writes to a copy would be lost.
    static void WriteProperty(ref MessageWriter writer, string property, List<string> items, List<string> hosts)
    {
        switch (property)
        {
            case "RegisteredStatusNotifierItems": writer.WriteVariant(VariantValue.Array(items.ToArray())); break;
            case "IsStatusNotifierHostRegistered": writer.WriteVariantBool(hosts.Count > 0); break;
            default: writer.WriteVariantInt32(0); break;
        }
    }

    readonly Dictionary<string, string> registeredBy = new();

    void RegisterItem(string service, string sender)
    {
        if (items.Contains(service) || registeredBy.Values.Count(s => s == sender) >= 16)
            return;
        items.Add(service);
        registeredBy[service] = sender;
        nameWatch ??= WatchOwners();
        connection.EmitSignal(TrayHost.WatcherPath, TrayHost.WatcherName, "StatusNotifierItemRegistered", "s", (ref MessageWriter w) => w.WriteString(service));
        ItemRegistered?.Invoke(service);
    }

    /// <summary>Unregisters items whose bus name disappears (the app exited).</summary>
    IDisposable WatchOwners()
    {
        var subscription = connection.WatchSignalAsync(App, "org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "NameOwnerChanged",
            (message, _) =>
            {
                var reader = message.GetBodyReader();
                string name = reader.ReadString();
                reader.ReadString();
                return (Name: name, NewOwner: reader.ReadString());
            },
            change =>
            {
                if (change.NewOwner.Length > 0)
                    return;
                foreach (var service in items.Where(s => s == change.Name || s.StartsWith(change.Name + "/")
                    || registeredBy.GetValueOrDefault(s) == change.Name).ToList())
                {
                    items.Remove(service);
                    registeredBy.Remove(service);
                    connection.EmitSignal(TrayHost.WatcherPath, TrayHost.WatcherName, "StatusNotifierItemUnregistered", "s", (ref MessageWriter w) => w.WriteString(service));
                    ItemUnregistered?.Invoke(service);
                }
                hosts.Remove(change.Name);
            });
        return new DeferredDisposable(subscription);
    }

    protected override string IntrospectionXml => """
        <interface name="org.kde.StatusNotifierWatcher">
          <method name="RegisterStatusNotifierItem"><arg direction="in" type="s"/></method>
          <method name="RegisterStatusNotifierHost"><arg direction="in" type="s"/></method>
          <property name="RegisteredStatusNotifierItems" type="as" access="read"/>
          <property name="IsStatusNotifierHostRegistered" type="b" access="read"/>
          <property name="ProtocolVersion" type="i" access="read"/>
          <signal name="StatusNotifierItemRegistered"><arg type="s"/></signal>
          <signal name="StatusNotifierItemUnregistered"><arg type="s"/></signal>
          <signal name="StatusNotifierHostRegistered"/>
        </interface>
        """;

    public void Dispose()
    {
        nameWatch?.Dispose();
        connection.RemoveMethodHandler(TrayHost.WatcherPath);
    }

    /// <summary>Disposes a subscription that may still be in flight.</summary>
    sealed class DeferredDisposable(Task<IDisposable> pending) : IDisposable
    {
        public void Dispose() => pending.ContinueWith(t => t.Result.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);
    }
}
