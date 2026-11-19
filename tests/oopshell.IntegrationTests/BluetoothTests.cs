using oopshell.Services.Bluetooth;
using oopshell.Services.DBus;
using Tmds.DBus.Protocol;

namespace oopshell.IntegrationTests;

[Collection("sway")]
public class BluetoothTests(SwayFixture sway)
{
    /// <summary>BlueZ's root object: GetManagedObjects from the fake objects' current properties.</summary>
    private sealed class FakeObjectManager(OopsApp app, List<FakeDBusObject> objects) : ExportedObject(app, "/")
    {
        protected override void HandleMethod(MethodContext context)
        {
            if (context.Request.MemberAsString != "GetManagedObjects")
                return;
            using var w = context.CreateReplyWriter("a{oa{sa{sv}}}");
            var all = w.WriteDictionaryStart();
            foreach (var o in objects)
            {
                w.WriteDictionaryEntryStart();
                w.WriteObjectPath(o.Path);
                var ifaces = w.WriteDictionaryStart();
                foreach (var (iface, props) in o.Interfaces)
                {
                    w.WriteDictionaryEntryStart();
                    w.WriteString(iface);
                    w.WriteDictionary(props);
                }
                w.WriteDictionaryEnd(ifaces);
            }
            w.WriteDictionaryEnd(all);
            context.Reply(w.CreateMessage());
        }
    }

    [Fact]
    public void Adapters_devices_and_actions()
    {
        using var app = sway.Connect();
        using var bus = new DBusDaemon();
        var bluez = app.Complete(OopsDBus.ConnectAsync(app, bus.Address));
        var objects = new List<FakeDBusObject>();
        FakeDBusObject Add(string path)
        {
            var o = new FakeDBusObject(app, path) { Connection = bluez };
            bluez.AddMethodHandler(o);
            objects.Add(o);
            return o;
        }
        bluez.AddMethodHandler(new FakeObjectManager(app, objects));

        var hci0 = Add("/org/bluez/hci0").With("org.bluez.Adapter1", new()
        {
            ["Address"] = "00:11:22:33:44:55",
            ["Alias"] = "testhost",
            ["Powered"] = true,
            ["Discovering"] = false,
            ["Discoverable"] = false,
        });
        var mouse = Add("/org/bluez/hci0/dev_AA").With("org.bluez.Device1", new()
        {
            ["Address"] = "AA:AA:AA:AA:AA:AA",
            ["Alias"] = "Mouse",
            ["Icon"] = "input-mouse",
            ["Paired"] = true,
            ["Trusted"] = true,
            ["Connected"] = false,
            ["Adapter"] = new ObjectPath("/org/bluez/hci0"),
        }).With("org.bluez.Battery1", new() { ["Percentage"] = (byte)76 });
        var headphones = Add("/org/bluez/hci0/dev_BB").With("org.bluez.Device1", new()
        {
            ["Address"] = "BB:BB:BB:BB:BB:BB",
            ["Alias"] = "Headphones",
            ["Icon"] = "audio-headset",
            ["Paired"] = true,
            ["Trusted"] = true,
            ["Connected"] = true,
            ["Adapter"] = new ObjectPath("/org/bluez/hci0"),
        });
        hci0.Methods["StartDiscovery"] = c =>
        {
            FakeDBusObject.ReplyEmpty(c);
            hci0.Set("org.bluez.Adapter1", "Discovering", true);
            // A device shows up while discovering.
            var found = Add("/org/bluez/hci0/dev_CC").With("org.bluez.Device1", new()
            {
                ["Address"] = "CC:CC:CC:CC:CC:CC",
                ["Alias"] = "CC-CC-CC-CC-CC-CC",
                ["Paired"] = false,
                ["Trusted"] = false,
                ["Connected"] = false,
                ["RSSI"] = (VariantValue)(short)-60,
                ["Adapter"] = new ObjectPath("/org/bluez/hci0"),
            });
            bluez.EmitSignal("/", "org.freedesktop.DBus.ObjectManager", "InterfacesAdded", "oa{sa{sv}}", (ref MessageWriter w) =>
            {
                w.WriteObjectPath(found.Path);
                var d = w.WriteDictionaryStart();
                w.WriteDictionaryEnd(d); // the service re-reads everything anyway
            });
        };
        var removed = new List<string>();
        hci0.Methods["RemoveDevice"] = c =>
        {
            removed.Add(c.Request.GetBodyReader().ReadObjectPathAsString());
            FakeDBusObject.ReplyEmpty(c);
        };
        mouse.Methods["Connect"] = c =>
        {
            FakeDBusObject.ReplyEmpty(c);
            mouse.Set("org.bluez.Device1", "Connected", true);
        };
        app.Complete(bluez.RequestNameOrThrowAsync(BluetoothService.BusName).ContinueWith(t => t.Exception is null));

        var shellBus = app.Complete(OopsDBus.ConnectAsync(app, bus.Address));
        using var bluetooth = app.Complete(BluetoothService.StartAsync(app, shellBus));

        Assert.Equal("testhost", bluetooth.Adapter!.Name);
        Assert.True(bluetooth.Adapter.Powered);
        // Connected first, then paired.
        Assert.Equal(["Headphones", "Mouse"], bluetooth.Devices.Select(d => d.Name));
        var mouseDevice = bluetooth.Devices[1];
        Assert.Equal((76, "input-mouse"), (mouseDevice.Battery!.Value, mouseDevice.Icon));
        Assert.Null(bluetooth.Devices[0].Battery);

        app.Complete(bluetooth.ConnectAsync(mouseDevice).ContinueWith(t => t.Exception is null));
        app.RunUntil(() => mouseDevice.Connected, what: "mouse connected");
        Assert.Same(mouseDevice, bluetooth.Devices.First(d => d.Name == "Mouse")); // updated in place

        app.Complete(bluetooth.StartDiscoveryAsync(bluetooth.Adapter).ContinueWith(t => t.Exception is null));
        app.RunUntil(() => bluetooth.Adapter.Discovering && bluetooth.Devices.Count == 3, what: "discovered device");
        var found = bluetooth.Devices.Last();
        Assert.Equal(("CC-CC-CC-CC-CC-CC", false, -60), (found.Name, found.Paired, found.Rssi!.Value));

        app.Complete(bluetooth.SetPoweredAsync(bluetooth.Adapter, false).ContinueWith(t => t.Exception is null));
        app.RunUntil(() => !bluetooth.Adapter.Powered, what: "powered off");
        app.Complete(bluetooth.ForgetAsync(mouseDevice).ContinueWith(t => t.Exception is null));
        Assert.Equal(["/org/bluez/hci0/dev_AA"], removed);
    }

    [Fact]
    public void The_pairing_agent_asks_and_answers()
    {
        using var app = sway.Connect();
        using var bus = new DBusDaemon();
        var bluez = app.Complete(OopsDBus.ConnectAsync(app, bus.Address));
        var objects = new List<FakeDBusObject>();
        bluez.AddMethodHandler(new FakeObjectManager(app, objects));
        var agentManager = new FakeDBusObject(app, "/org/bluez");
        var calls = new List<string>();
        foreach (var method in new[] { "RegisterAgent", "RequestDefaultAgent", "UnregisterAgent" })
            agentManager.Methods[method] = c =>
            {
                calls.Add(method);
                FakeDBusObject.ReplyEmpty(c);
            };
        bluez.AddMethodHandler(agentManager);
        app.Complete(bluez.RequestNameOrThrowAsync(BluetoothService.BusName).ContinueWith(t => t.Exception is null));

        var shellBus = app.Complete(OopsDBus.ConnectAsync(app, bus.Address));
        using var bluetooth = app.Complete(BluetoothService.StartAsync(app, shellBus));
        var requests = new List<BluetoothPairingRequest>();
        bluetooth.PairingRequested += requests.Add;
        app.Complete(bluetooth.RegisterPairingAgentAsync().ContinueWith(t => t.Exception is null));
        Assert.Equal(["RegisterAgent", "RequestDefaultAgent"], calls);

        Task<T> AgentReply<T>(string method, string signature, BodyWriter args, MessageValueReader<T> read) =>
            bluez.CallAsync(shellBus.UniqueName!, "/org/oopshell/bluez/agent", "org.bluez.Agent1", method, read, args, signature);
        Task Agent(string method, string signature, BodyWriter args) =>
            bluez.CallAsync(shellBus.UniqueName!, "/org/oopshell/bluez/agent", "org.bluez.Agent1", method, args, signature);
        const string Dev = "/org/bluez/hci0/dev_DD";

        // Compare a passkey, accept.
        var confirm = Agent("RequestConfirmation", "ou", (ref MessageWriter w) => { w.WriteObjectPath(Dev); w.WriteUInt32(42); });
        app.RunUntil(() => requests.Count == 1, what: "confirmation");
        Assert.Equal((BluetoothPairingKind.ConfirmPasskey, 42u, Dev), (requests[0].Kind, requests[0].Passkey!.Value, requests[0].DevicePath));
        requests[0].Accept();
        Assert.True(app.Complete(confirm.ContinueWith(t => t.Exception is null)));

        // Type a PIN.
        var pin = AgentReply("RequestPinCode", "o", (ref MessageWriter w) => w.WriteObjectPath(Dev), (m, _) => m.GetBodyReader().ReadString());
        app.RunUntil(() => requests.Count == 2, what: "PIN request");
        requests[1].Provide("0000");
        Assert.Equal("0000", app.Complete(pin));

        // A passkey to show needs no answer; BlueZ cancelling ends it.
        app.Complete(Agent("DisplayPasskey", "ouq", (ref MessageWriter w) => { w.WriteObjectPath(Dev); w.WriteUInt32(123456); w.WriteUInt16(0); })
            .ContinueWith(t => t.Exception is null));
        Assert.Equal((BluetoothPairingKind.ShowPasskey, false), (requests[2].Kind, requests[2].NeedsAnswer));

        // A rejected authorization comes back as BlueZ's Rejected error.
        var authorize = Agent("RequestAuthorization", "o", (ref MessageWriter w) => w.WriteObjectPath(Dev));
        app.RunUntil(() => requests.Count == 4, what: "authorization");
        requests[3].Reject();
        var error = Assert.IsType<DBusErrorReplyException>(app.Complete(authorize.ContinueWith(t => t.Exception?.InnerException)));
        Assert.Equal("org.bluez.Error.Rejected", error.ErrorName);

        bluetooth.Dispose();
        app.RunUntil(() => calls.Contains("UnregisterAgent"), what: "unregistered");
    }

    [Fact]
    public void This_machines_BlueZ_reads()
    {
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
        BluetoothService bluetooth;
        try
        {
            bluetooth = app.Complete(BluetoothService.StartAsync(app, system));
        }
        catch (DBusErrorReplyException)
        {
            Assert.Skip("bluetoothd is not running.");
            return;
        }
        using (bluetooth)
        {
            Assert.All(bluetooth.Adapters, a => Assert.Matches("^([0-9A-F]{2}:){5}[0-9A-F]{2}$", a.Address));
            Assert.All(bluetooth.Devices, d => Assert.NotEmpty(d.Name));
        }
    }
}
