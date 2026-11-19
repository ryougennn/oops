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
    /// <summary>Largest image accepted, in pixels per side.</summary>
    public const int MaxImageSide = 1024;

    private const string Interface = "org.freedesktop.Notifications";
    private const string ObjectPath = "/org/freedesktop/Notifications";

    private readonly DBusConnection _connection;
    private readonly ObservableCollection<Notification> _notifications = new();
    private readonly Dictionary<uint, IDisposable> _expiryTimers = new();
    private uint _nextId = 1;

    private NotificationServer(OopsApp app, DBusConnection connection) : base(app, ObjectPath)
    {
        _connection = connection;
        Notifications = new ReadOnlyObservableCollection<Notification>(_notifications);
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

    /// <summary>Maximum number of notifications kept; beyond it the oldest expires, so one app cannot flood the shell.</summary>
    public int MaxNotifications { get; set; } = 200;

    /// <summary>A new notification arrived (it is already in <see cref="Notifications"/>).</summary>
    public event Action<Notification>? Added;

    /// <summary>Closes a notification and tells its app why.</summary>
    public void Close(Notification notification, CloseReason reason = CloseReason.Dismissed) => Close(notification.Id, reason);

    /// <summary>
    /// Reports that the user chose an action. Non-resident notifications close afterwards. Pass the surface
    /// that was clicked (the bubble) as <paramref name="from"/>: the app then also gets an activation token
    /// (the spec's ActivationToken signal, sent first), which compositors that prevent focus stealing require
    /// before the app may raise its window. The signals go out once the compositor has issued the token.
    /// </summary>
    public void InvokeAction(Notification notification, string actionKey, Surface? from = null) =>
        _ = InvokeActionAsync(notification, actionKey, from);

    private async Task InvokeActionAsync(Notification notification, string actionKey, Surface? from)
    {
        string? token = from is null ? null
            : await App.RequestActivationTokenAsync(new LaunchOptions { Surface = from, AppId = notification.DesktopEntry });
        if (token is not null)
        {
            _connection.EmitSignal(ObjectPath, Interface, "ActivationToken", "us", (ref MessageWriter w) =>
            {
                w.WriteUInt32(notification.Id);
                w.WriteString(token);
            });
        }
        _connection.EmitSignal(ObjectPath, Interface, "ActionInvoked", "us", (ref MessageWriter w) =>
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

    /// <summary>The next id; 0 means "new" in the protocol, so it is skipped when the counter wraps.</summary>
    private uint NextId()
    {
        if (_nextId == 0)
            _nextId = 1;
        return _nextId++;
    }

    private uint Notify(Reader reader)
    {
        // Untrusted input: read and validate everything first, so a malformed call cannot half-update the
        // notification it replaces.
        string appName = reader.ReadString().Clip(256);
        uint replacesId = reader.ReadUInt32();
        string appIcon = reader.ReadString().Clip(4096);
        string summary = reader.ReadString().Clip(1024);
        string body = reader.ReadString().Clip(16384);
        var actionStrings = reader.ReadArrayOfString();
        var hints = reader.ReadDictionaryOfStringToVariantValue();
        int expireMs = reader.ReadInt32();
        var actions = Enumerable.Range(0, Math.Min(actionStrings.Length / 2, 32))
            .Select(i => new NotificationAction(actionStrings[2 * i].Clip(256), actionStrings[2 * i + 1].Clip(256))).ToList();
        var parsed = Hints.Parse(hints);

        var existing = replacesId == 0 ? null : _notifications.FirstOrDefault(n => n.Id == replacesId);
        var notification = existing ?? new Notification(NextId());
        notification.AppName = appName;
        notification.AppIcon = appIcon;
        notification.Summary = summary;
        notification.Body = body;
        notification.Actions = actions;
        notification.ExpireTimeout = expireMs < 0 ? null : TimeSpan.FromMilliseconds(expireMs);
        parsed.ApplyTo(notification);

        if (existing is null)
        {
            _notifications.Add(notification);
            while (_notifications.Count > Math.Max(1, MaxNotifications))
                Close(_notifications[0].Id, CloseReason.Expired);
            Added?.Invoke(notification);
        }
        else
        {
            notification.RaiseChanged();
        }
        ScheduleExpiry(notification);
        return notification.Id;
    }

    /// <summary>The supported hints. A hint of the wrong type is ignored.</summary>
    private readonly record struct Hints(Urgency Urgency, string? Category, string? DesktopEntry, bool Resident, bool Transient, string? ImagePath, NotificationImage? Image)
    {
        public static Hints Parse(Dictionary<string, VariantValue> hints)
        {
            VariantValue? Get(params string[] names)
            {
                foreach (var name in names)
                {
                    if (hints.TryGetValue(name, out var v))
                        return v;
                }
                return null;
            }
            long urgency = Get("urgency")?.AsInt64() ?? 1;
            return new Hints(
                (Urgency)Math.Clamp(urgency, 0, 2),
                Get("category")?.AsString()?.Clip(256),
                Get("desktop-entry")?.AsString()?.Clip(256),
                Get("resident")?.AsBool() ?? false,
                Get("transient")?.AsBool() ?? false,
                Get("image-path", "image_path")?.AsString()?.Clip(4096),
                Get("image-data", "image_data", "icon_data") is { } image ? ReadImage(image) : null);
        }

        public void ApplyTo(Notification n)
        {
            n.Urgency = Urgency;
            n.Category = Category;
            n.DesktopEntry = DesktopEntry;
            n.Resident = Resident;
            n.Transient = Transient;
            n.ImagePath = ImagePath;
            n.Image = Image;
        }
    }

    /// <summary>
    /// Decodes the <c>(iiibiiay)</c> image struct. Null unless it is consistent: 1..1024 pixels per side, 8 bits
    /// per sample, 3 or 4 channels matching HasAlpha, a row stride that fits a row, and enough data for every row.
    /// </summary>
    private static NotificationImage? ReadImage(VariantValue value)
    {
        if (value.Field(0)?.AsInt32() is not int width || value.Field(1)?.AsInt32() is not int height
            || value.Field(2)?.AsInt32() is not int stride || value.Field(3)?.AsBool() is not bool alpha
            || value.Field(4)?.AsInt32() is not int bits || value.Field(5)?.AsInt32() is not int channels
            || value.Field(6)?.AsBytes() is not { } data)
            return null;
        if (width is < 1 or > MaxImageSide || height is < 1 or > MaxImageSide || bits != 8 || channels != (alpha ? 4 : 3)
            || stride < width * channels || stride > width * channels + 64 || data.Length < (long)(height - 1) * stride + width * channels)
            return null;
        return new NotificationImage(width, height, stride, alpha, bits, channels, data);
    }

    private void ScheduleExpiry(Notification notification)
    {
        if (_expiryTimers.Remove(notification.Id, out var old))
            old.Dispose();
        var timeout = notification.ExpireTimeout ?? DefaultTimeout;
        if (timeout == TimeSpan.Zero || notification.Urgency == Urgency.Critical && notification.ExpireTimeout is null)
            return; // Critical notifications without an explicit timeout stay until dismissed.
        uint id = notification.Id;
        _expiryTimers[id] = App.SetTimer(timeout, null, () => Close(id, CloseReason.Expired));
    }

    private void Close(uint id, CloseReason reason)
    {
        var notification = _notifications.FirstOrDefault(n => n.Id == id);
        if (notification is null)
            return;
        if (_expiryTimers.Remove(id, out var timer))
            timer.Dispose();
        _notifications.Remove(notification);
        notification.IsClosed = true;
        notification.RaiseClosed(reason);
        _connection.EmitSignal(ObjectPath, Interface, "NotificationClosed", "uu", (ref MessageWriter w) =>
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
          <signal name="ActivationToken"><arg type="u"/><arg type="s"/></signal>
        </interface>
        """;

    public void Dispose()
    {
        foreach (var timer in _expiryTimers.Values)
            timer.Dispose();
        _expiryTimers.Clear();
        _connection.RemoveMethodHandler(ObjectPath);
    }
}
