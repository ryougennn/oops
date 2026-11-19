using System.Text;
using oopshell.Services.DBus;
using oopshell.Services.Network;
using Tmds.DBus.Protocol;

namespace oopshell.IntegrationTests;

[Collection("sway")]
public class NetworkTests(SwayFixture sway)
{
    const string Nm = "org.freedesktop.NetworkManager";
    const string Root = "/org/freedesktop/NetworkManager";

    static VariantValue Paths(params string[] paths) => new Array<ObjectPath>(paths.Select(p => new ObjectPath(p)));
    static VariantValue Ssid(string s) => new Array<byte>(Encoding.UTF8.GetBytes(s));

    static void ReplyEmpty(MethodContext c)
    {
        using var w = c.CreateReplyWriter(null);
        c.Reply(w.CreateMessage());
    }

    [Fact]
    public void Devices_networks_and_wifi_actions()
    {
        using var app = sway.Connect();
        using var bus = new DBusDaemon();
        var nmBus = app.Complete(OopsDBus.ConnectAsync(app, bus.Address));
        FakeDBusObject Add(string path)
        {
            var o = new FakeDBusObject(app, path) { Connection = nmBus };
            nmBus.AddMethodHandler(o);
            return o;
        }

        var manager = Add(Root).With(Nm, new()
        {
            ["Devices"] = Paths($"{Root}/Devices/1", $"{Root}/Devices/2", $"{Root}/Devices/3"),
            ["Connectivity"] = 4u, ["NetworkingEnabled"] = true, ["WirelessEnabled"] = true, ["WirelessHardwareEnabled"] = true,
            ["PrimaryConnection"] = new ObjectPath($"{Root}/ActiveConnection/1"),
        });
        Add($"{Root}/Devices/1").With($"{Nm}.Device", new() { ["Interface"] = "lo", ["DeviceType"] = 32u, ["State"] = 100u, ["ActiveConnection"] = new ObjectPath("/") });
        var wired = Add($"{Root}/Devices/2").With($"{Nm}.Device", new() { ["Interface"] = "enp42s0", ["DeviceType"] = 1u, ["State"] = 100u, ["ActiveConnection"] = new ObjectPath($"{Root}/ActiveConnection/1") });
        var wifi = Add($"{Root}/Devices/3")
            .With($"{Nm}.Device", new() { ["Interface"] = "wlo1", ["DeviceType"] = 2u, ["State"] = 30u, ["ActiveConnection"] = new ObjectPath("/") })
            .With($"{Nm}.Device.Wireless", new()
            {
                ["AccessPoints"] = Paths($"{Root}/AccessPoint/1", $"{Root}/AccessPoint/2", $"{Root}/AccessPoint/3", $"{Root}/AccessPoint/4"),
                ["ActiveAccessPoint"] = new ObjectPath("/"),
            });
        Add($"{Root}/ActiveConnection/1").With($"{Nm}.Connection.Active", new() { ["Id"] = "Wired connection 1", ["Type"] = "802-3-ethernet" });
        Add($"{Root}/AccessPoint/1").With($"{Nm}.AccessPoint", new() { ["Ssid"] = Ssid("Home"), ["Strength"] = (byte)60, ["Flags"] = 1u, ["WpaFlags"] = 0u, ["RsnFlags"] = 0x188u, ["Frequency"] = 5180u });
        Add($"{Root}/AccessPoint/2").With($"{Nm}.AccessPoint", new() { ["Ssid"] = Ssid("Home"), ["Strength"] = (byte)80, ["Flags"] = 1u, ["WpaFlags"] = 0u, ["RsnFlags"] = 0x188u, ["Frequency"] = 2437u });
        Add($"{Root}/AccessPoint/3").With($"{Nm}.AccessPoint", new() { ["Ssid"] = Ssid("Café"), ["Strength"] = (byte)40, ["Flags"] = 0u, ["WpaFlags"] = 0u, ["RsnFlags"] = 0u, ["Frequency"] = 2412u });
        Add($"{Root}/AccessPoint/4").With($"{Nm}.AccessPoint", new() { ["Ssid"] = Ssid(""), ["Strength"] = (byte)90, ["Flags"] = 1u, ["WpaFlags"] = 0u, ["RsnFlags"] = 0x188u, ["Frequency"] = 2412u });

        var settings = Add($"{Root}/Settings");
        settings.Methods["ListConnections"] = c =>
        {
            using var w = c.CreateReplyWriter("ao");
            w.WriteArray(new[] { new ObjectPath($"{Root}/Settings/1") });
            c.Reply(w.CreateMessage());
        };
        var saved = Add($"{Root}/Settings/1");
        saved.Methods["GetSettings"] = c =>
        {
            using var w = c.CreateReplyWriter("a{sa{sv}}");
            var d = w.WriteDictionaryStart();
            w.WriteDictionaryEntryStart();
            w.WriteString("802-11-wireless");
            w.WriteDictionary(new Dictionary<string, VariantValue> { ["ssid"] = Ssid("Home") });
            w.WriteDictionaryEnd(d);
            c.Reply(w.CreateMessage());
        };
        var activated = new List<string>();
        manager.Methods["ActivateConnection"] = c =>
        {
            activated.Add(c.Request.GetBodyReader().ReadObjectPathAsString());
            using var w = c.CreateReplyWriter("o");
            w.WriteObjectPath($"{Root}/ActiveConnection/9");
            c.Reply(w.CreateMessage());
        };
        Dictionary<string, Dictionary<string, VariantValue>>? added = null;
        manager.Methods["AddAndActivateConnection"] = c =>
        {
            var r = c.Request.GetBodyReader();
            added = new();
            var end = r.ReadDictionaryStart();
            while (r.HasNext(end))
                added[r.ReadString()] = r.ReadDictionaryOfStringToVariantValue();
            using var w = c.CreateReplyWriter("oo");
            w.WriteObjectPath($"{Root}/Settings/2");
            w.WriteObjectPath($"{Root}/ActiveConnection/10");
            c.Reply(w.CreateMessage());
        };
        wifi.Methods["Disconnect"] = ReplyEmpty;
        wifi.Methods["RequestScan"] = ReplyEmpty;
        app.Complete(nmBus.RequestNameOrThrowAsync(Nm).ContinueWith(t => t.Exception is null));

        var shellBus = app.Complete(OopsDBus.ConnectAsync(app, bus.Address));
        using var network = app.Complete(NetworkService.StartAsync(app, shellBus));

        Assert.Equal(["enp42s0", "wlo1"], network.Devices.Select(d => d.Interface)); // no loopback
        Assert.Equal(NetworkConnectivity.Full, network.Connectivity);
        Assert.Equal(("Wired connection 1", NetworkDeviceType.Ethernet), (network.PrimaryConnectionName, network.PrimaryConnectionType));
        Assert.Equal("Wired connection 1", network.Devices[0].ConnectionName);
        var wlo1 = network.Wifi!;
        Assert.Equal(NetworkDeviceState.Disconnected, wlo1.State);
        // One entry per name (the strongest), hidden networks left out, strongest first.
        Assert.Equal([("Home", 80, true), ("Café", 40, false)], wlo1.AccessPoints.Select(a => (a.Ssid, a.Strength, a.Secured)));

        // A saved network reuses its connection; a new one is created (open: no security settings).
        app.Complete(network.ConnectAsync(wlo1, wlo1.AccessPoints[0]).ContinueWith(t => t.Exception is null));
        Assert.Equal([$"{Root}/Settings/1"], activated);
        Assert.True(app.Complete(network.ConnectAsync(wlo1, wlo1.AccessPoints[1]).ContinueWith(t => t.Exception is null)));
        Assert.Equal("Café", Encoding.UTF8.GetString(added!["802-11-wireless"]["ssid"].GetArray<byte>()));
        Assert.False(added.ContainsKey("802-11-wireless-security"));

        // Changes NetworkManager announces show up.
        int changes = 0;
        network.Changed += () => changes++;
        app.Complete(network.SetWifiEnabledAsync(false).ContinueWith(t => t.Exception is null));
        app.RunUntil(() => !network.WifiEnabled, what: "wifi off");
        wifi.Set($"{Nm}.Device", "State", 100u);
        wifi.Set($"{Nm}.Device.Wireless", "ActiveAccessPoint", new ObjectPath($"{Root}/AccessPoint/3"));
        app.RunUntil(() => wlo1.State == NetworkDeviceState.Connected && wlo1.ActiveAccessPoint?.Ssid == "Café", what: "connected");
        Assert.Equal("Café", wlo1.AccessPoints[0].Ssid); // the network in use comes first
        Assert.True(changes >= 1);

        app.Complete(network.DisconnectAsync(wlo1).ContinueWith(t => t.Exception is null));
        app.Complete(network.ScanAsync(wlo1).ContinueWith(t => t.Exception is null));
        Assert.Contains("Disconnect", wifi.Calls);
        Assert.Contains("RequestScan", wifi.Calls);
    }

    [Fact]
    public void This_machines_NetworkManager_reads()
    {
        // Read-only against the real NetworkManager, when there is one.
        using var app = sway.Connect();
        DBusConnection system;
        try
        {
            system = app.Complete(OopsDBus.ConnectAsync(app, DBusAddress.System));
        }
        catch (Exception e) when (e is DBusConnectionException or IOException)
        {
            Assert.Skip("No system bus.");
            return;
        }
        NetworkService network;
        try
        {
            network = app.Complete(NetworkService.StartAsync(app, system));
        }
        catch (DBusErrorReplyException)
        {
            Assert.Skip("NetworkManager is not running.");
            return;
        }
        using (network)
        {
            Assert.All(network.Devices, d => Assert.NotEmpty(d.Interface));
            Assert.DoesNotContain(network.Devices, d => d.Type == NetworkDeviceType.Loopback);
            if (network.Devices.Any(d => d.State == NetworkDeviceState.Connected))
                Assert.NotNull(network.PrimaryConnectionName);
        }
    }
}
