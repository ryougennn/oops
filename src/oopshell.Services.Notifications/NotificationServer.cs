using System.Collections.ObjectModel;
using oopshell.Services.DBus;
using Tmds.DBus.Protocol;

namespace oopshell.Services.Notifications;

/// <summary>
/// The <c>org.freedesktop.Notifications</c> server: owns the bus name, keeps the live notifications,
/// expires them, and reports closes and action invocations back to the apps. Drawing them is up to you.
/// </summary>
public sealed class NotificationServer : ExportedObject, IDisposable
{
    const string Interface = "org.freedesktop.Notifications";
    const string ObjectPath = "/org/freedesktop/Notifications";

    readonly DBusConnection connection;
    readonly ObservableCollection<Notification> notifications = new();
    readonly Dictionary<uint, IDisposable> expiryTimers = new();
    uint nextId = 1;

    NotificationServer(OopsApp app, DBusConnection connection) : base(app, ObjectPath)
    {
        this.connection = connection;
        Notifications = new ReadOnlyObservableCollection<Notification>(notifications);
    }

    /// <summary>Exports the server and takes the bus name. Fails if another notification daemon runs.</summary>
    public static async Task<NotificationServer> StartAsync(OopsApp app, DBusConnection connection)
    {
        var server = new NotificationServer(app, connection);
        connection.AddMethodHandler(server);
        await connection.RequestNameOrThrowAsync(Interface);
        return server;
    }

    /// <summary>Currently shown notifications, oldest first.</summary>
    public ReadOnlyObservableCollection<Notification> Notifications { get; }

    /// <summary>Display time when the app asks for the server default.</summary>
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Capabilities announced to apps.</summary>
    public IReadOnlyList<string> Capabilities { get; set; } = ["actions", "body", "body-markup", "icon-static", "persistence"];

    public string ServerName { get; set; } = "oopshell";

    /// <summary>A new notification arrived (it is already in <see cref="Notifications"/>).</summary>
    public event Action<Notification>? Added;

    /// <summary>Closes a notification and tells its app why.</summary>
    public void Close(Notification notification, CloseReason reason = CloseReason.Dismissed) => Close(notification.Id, reason);

    /// <summary>Reports that the user chose an action. Non-resident notifications close afterwards.</summary>
    public void InvokeAction(Notification notification, string actionKey)
    {
        connection.EmitSignal(ObjectPath, Interface, "ActionInvoked", "us", (ref MessageWriter w) =>
        {
            w.WriteUInt32(notification.Id);
            w.WriteString(actionKey);
        });
        if (!notification.Resident)
            Close(notification.Id, CloseReason.Dismissed);
    }

    protected override void HandleMethod(MethodContext context)
    {
        if (context.Request.InterfaceAsString != Interface)
            return;
        switch (context.Request.MemberAsString)
        {
            case "GetCapabilities":
            {
                using var writer = context.CreateReplyWriter("as");
                writer.WriteArray(Capabilities.ToArray());
                context.Reply(writer.CreateMessage());
                break;
            }
            case "GetServerInformation":
            {
                using var writer = context.CreateReplyWriter("ssss");
                writer.WriteString(ServerName);
                writer.WriteString("oopshell");
                writer.WriteString("1.0");
                writer.WriteString("1.2");
                context.Reply(writer.CreateMessage());
                break;
            }
            case "Notify":
            {
                uint id = Notify(context.Request.GetBodyReader());
                using var writer = context.CreateReplyWriter("u");
                writer.WriteUInt32(id);
                context.Reply(writer.CreateMessage());
                break;
            }
            case "CloseNotification":
            {
                uint id = context.Request.GetBodyReader().ReadUInt32();
                Close(id, CloseReason.ClosedByCall);
                ReplyEmpty(context);
                break;
            }
        }
    }

    uint Notify(Reader reader)
    {
        string appName = reader.ReadString();
        uint replacesId = reader.ReadUInt32();
        string appIcon = reader.ReadString();
        string summary = reader.ReadString();
        string body = reader.ReadString();
        var actionStrings = reader.ReadArrayOfString();
        var hints = reader.ReadDictionaryOfStringToVariantValue();
        int expireMs = reader.ReadInt32();

        var existing = replacesId == 0 ? null : notifications.FirstOrDefault(n => n.Id == replacesId);
        var notification = existing ?? new Notification(nextId++);
        notification.AppName = appName;
        notification.AppIcon = appIcon;
        notification.Summary = summary;
        notification.Body = body;
        notification.Actions = Enumerable.Range(0, actionStrings.Length / 2)
            .Select(i => new NotificationAction(actionStrings[2 * i], actionStrings[2 * i + 1])).ToList();
        notification.ExpireTimeout = expireMs < 0 ? null : TimeSpan.FromMilliseconds(expireMs);
        ApplyHints(notification, hints);

        if (existing is null)
        {
            notifications.Add(notification);
            Added?.Invoke(notification);
        }
        else
        {
            notification.RaiseChanged();
        }
        ScheduleExpiry(notification);
        return notification.Id;
    }

    static void ApplyHints(Notification n, Dictionary<string, VariantValue> hints)
    {
        if (hints.TryGetValue("urgency", out var urgency))
            n.Urgency = (Urgency)urgency.GetByte();
        n.Category = hints.TryGetValue("category", out var category) ? category.GetString() : null;
        n.DesktopEntry = hints.TryGetValue("desktop-entry", out var entry) ? entry.GetString() : null;
        n.Resident = hints.TryGetValue("resident", out var resident) && resident.GetBool();
        n.Transient = hints.TryGetValue("transient", out var transient) && transient.GetBool();
        n.ImagePath = hints.TryGetValue("image-path", out var path) || hints.TryGetValue("image_path", out path) ? path.GetString() : null;
        n.Image = hints.TryGetValue("image-data", out var image) || hints.TryGetValue("image_data", out image) || hints.TryGetValue("icon_data", out image)
            ? ReadImage(image)
            : null;
    }

    /// <summary>Decodes the <c>(iiibiiay)</c> image struct.</summary>
    static NotificationImage? ReadImage(VariantValue value)
    {
        try
        {
            return new NotificationImage(
                value.GetItem(0).GetInt32(), value.GetItem(1).GetInt32(), value.GetItem(2).GetInt32(),
                value.GetItem(3).GetBool(), value.GetItem(4).GetInt32(), value.GetItem(5).GetInt32(),
                value.GetItem(6).GetArray<byte>());
        }
        catch (InvalidOperationException)
        {
            return null; // malformed hint: ignore it rather than fail the notification
        }
    }

    void ScheduleExpiry(Notification notification)
    {
        if (expiryTimers.Remove(notification.Id, out var old))
            old.Dispose();
        var timeout = notification.ExpireTimeout ?? DefaultTimeout;
        if (timeout == TimeSpan.Zero || notification.Urgency == Urgency.Critical && notification.ExpireTimeout is null)
            return; // never expires (critical notifications stay until dismissed)
        uint id = notification.Id;
        expiryTimers[id] = App.SetTimer(timeout, null, () => Close(id, CloseReason.Expired));
    }

    void Close(uint id, CloseReason reason)
    {
        var notification = notifications.FirstOrDefault(n => n.Id == id);
        if (notification is null)
            return;
        if (expiryTimers.Remove(id, out var timer))
            timer.Dispose();
        notifications.Remove(notification);
        notification.IsClosed = true;
        notification.RaiseClosed(reason);
        connection.EmitSignal(ObjectPath, Interface, "NotificationClosed", "uu", (ref MessageWriter w) =>
        {
            w.WriteUInt32(id);
            w.WriteUInt32((uint)reason);
        });
    }

    protected override string IntrospectionXml => """
        <interface name="org.freedesktop.Notifications">
          <method name="GetCapabilities"><arg direction="out" type="as"/></method>
          <method name="Notify">
            <arg direction="in" type="s"/><arg direction="in" type="u"/><arg direction="in" type="s"/>
            <arg direction="in" type="s"/><arg direction="in" type="s"/><arg direction="in" type="as"/>
            <arg direction="in" type="a{sv}"/><arg direction="in" type="i"/><arg direction="out" type="u"/>
          </method>
          <method name="CloseNotification"><arg direction="in" type="u"/></method>
          <method name="GetServerInformation">
            <arg direction="out" type="s"/><arg direction="out" type="s"/><arg direction="out" type="s"/><arg direction="out" type="s"/>
          </method>
          <signal name="NotificationClosed"><arg type="u"/><arg type="u"/></signal>
          <signal name="ActionInvoked"><arg type="u"/><arg type="s"/></signal>
        </interface>
        """;

    public void Dispose()
    {
        foreach (var timer in expiryTimers.Values)
            timer.Dispose();
        expiryTimers.Clear();
        connection.RemoveMethodHandler(ObjectPath);
    }
}
