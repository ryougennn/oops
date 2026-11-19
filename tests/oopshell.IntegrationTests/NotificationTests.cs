using oopshell.Services.DBus;
using oopshell.Services.Notifications;

namespace oopshell.IntegrationTests;

[Collection("sway")]
public class NotificationTests(SwayFixture sway)
{
    [Fact]
    public void Notify_send_reaches_the_server_and_actions_flow_back()
    {
        Assert.SkipUnless(File.Exists("/usr/bin/notify-send"), "notify-send is not installed.");
        using var bus = new DBusDaemon();
        using var app = sway.Connect();
        var connection = app.Complete(OopsDBus.ConnectAsync(app, bus.Address));
        using var server = app.Complete(NotificationServer.StartAsync(app, connection));
        var closes = new List<(uint Id, CloseReason Reason)>();
        server.Added += n => n.Closed += reason => closes.Add((n.Id, reason));

        // -A implies --wait: notify-send prints the chosen action and exits.
        using var withAction = bus.Start("notify-send", "-a", "TestApp", "-u", "critical", "-h", "string:desktop-entry:foot",
            "-A", "ok=Okay", "Hello", "World <b>bold</b>");
        app.RunUntil(() => server.Notifications.Count == 1, what: "notification");
        var n = server.Notifications[0];
        Assert.Equal("TestApp", n.AppName);
        Assert.Equal("Hello", n.Summary);
        Assert.Equal("World <b>bold</b>", n.Body);
        Assert.Equal(Urgency.Critical, n.Urgency);
        Assert.Equal("foot", n.DesktopEntry);
        Assert.Equal([new NotificationAction("ok", "Okay")], n.Actions);

        server.InvokeAction(n, "ok");
        app.RunUntil(() => withAction.HasExited, what: "notify-send exit");
        Assert.Equal("ok", withAction.StandardOutput.ReadToEnd().Trim());
        Assert.Empty(server.Notifications);
        Assert.Equal([(n.Id, CloseReason.Dismissed)], closes);
    }

    [Fact]
    public void Unknown_methods_get_an_error_and_the_server_stays_connected()
    {
        // A malformed error reply once made the bus drop the server's connection on the first unknown call.
        using var bus = new DBusDaemon();
        using var app = sway.Connect();
        var connection = app.Complete(OopsDBus.ConnectAsync(app, bus.Address));
        using var server = app.Complete(NotificationServer.StartAsync(app, connection));
        var client = app.Complete(OopsDBus.ConnectAsync(app, bus.Address));
        const string Name = "org.freedesktop.Notifications", Path = "/org/freedesktop/Notifications";

        var error = app.Complete(client.CallAsync(Name, Path, Name, "NoSuchMethod").ContinueWith(t => t.Exception?.InnerException));
        Assert.Equal("org.freedesktop.DBus.Error.UnknownMethod", Assert.IsType<Tmds.DBus.Protocol.DBusErrorReplyException>(error).ErrorName);
        string serverName = app.Complete(client.CallAsync(Name, Path, Name, "GetServerInformation", (m, _) => m.GetBodyReader().ReadString()));
        Assert.Equal("oopshell", serverName);
    }

    [Fact]
    public void Notifications_expire_and_can_be_replaced()
    {
        Assert.SkipUnless(File.Exists("/usr/bin/notify-send"), "notify-send is not installed.");
        using var bus = new DBusDaemon();
        using var app = sway.Connect();
        var connection = app.Complete(OopsDBus.ConnectAsync(app, bus.Address));
        using var server = app.Complete(NotificationServer.StartAsync(app, connection));

        using var first = bus.Start("notify-send", "-p", "-t", "60000", "Version 1");
        app.RunUntil(() => first.HasExited && server.Notifications.Count == 1, what: "first notification");
        uint id = uint.Parse(first.StandardOutput.ReadToEnd().Trim());
        var notification = server.Notifications.Single();
        Assert.Equal(id, notification.Id);
        bool changed = false;
        notification.Changed += () => changed = true;

        using var replace = bus.Start("notify-send", "-r", id.ToString(), "-t", "300", "Version 2");
        app.RunUntil(() => changed, what: "replacement");
        Assert.Equal("Version 2", notification.Summary);
        Assert.Single(server.Notifications);

        CloseReason? reason = null;
        notification.Closed += r => reason = r;
        app.RunUntil(() => reason is not null, what: "expiry");
        Assert.Equal(CloseReason.Expired, reason);
    }
}
