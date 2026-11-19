using oopshell.Services.DBus;
using oopshell.Services.Power;
using Tmds.DBus.Protocol;

namespace oopshell.IntegrationTests;

[Collection("sway")]
public class PowerTests(SwayFixture sway)
{
    private const string DisplayPath = "/org/freedesktop/UPower/devices/DisplayDevice";
    private const string MousePath = "/org/freedesktop/UPower/devices/mouse_hidpp_battery_0";
    private const string LaptopPath = "/org/freedesktop/UPower/devices/battery_BAT0";

    /// <summary>A UPower device: its properties are a plain dictionary the test edits.</summary>
    private sealed class FakeDevice(OopsApp app, string path, Dictionary<string, object> properties) : ExportedObject(app, path)
    {
        public readonly Dictionary<string, object> Properties = properties;
        public int Reads;

        protected override void HandleMethod(MethodContext context)
        {
            if (context.Request.MemberAsString != "GetAll")
                return;
            Reads++;
            var writer = context.CreateReplyWriter("a{sv}");
            try
            {
                var dict = writer.WriteDictionaryStart();
                foreach (var (key, value) in Properties)
                {
                    writer.WriteDictionaryEntryStart();
                    writer.WriteString(key);
                    switch (value)
                    {
                        case uint u: writer.WriteVariantUInt32(u); break;
                        case double d: writer.WriteVariantDouble(d); break;
                        case bool b: writer.WriteVariantBool(b); break;
                        case long l: writer.WriteVariantInt64(l); break;
                        case string s: writer.WriteVariantString(s); break;
                    }
                }
                writer.WriteDictionaryEnd(dict);
                context.Reply(writer.CreateMessage());
            }
            finally
            {
                writer.Dispose();
            }
        }
    }

    private sealed class FakeUPower(OopsApp app) : ExportedObject(app, PowerService.ObjectPath)
    {
        public readonly List<string> Devices = new();
        public bool OnBattery;

        protected override void HandleMethod(MethodContext context)
        {
            switch (context.Request.MemberAsString)
            {
                case "EnumerateDevices":
                    {
                        var writer = context.CreateReplyWriter("ao");
                        try
                        {
                            var array = writer.WriteArrayStart(DBusType.ObjectPath);
                            foreach (var path in Devices)
                                writer.WriteObjectPath(path);
                            writer.WriteArrayEnd(array);
                            context.Reply(writer.CreateMessage());
                        }
                        finally
                        {
                            writer.Dispose();
                        }
                        break;
                    }
                case "GetDisplayDevice":
                    {
                        var writer = context.CreateReplyWriter("o");
                        try
                        {
                            writer.WriteObjectPath(DisplayPath);
                            context.Reply(writer.CreateMessage());
                        }
                        finally
                        {
                            writer.Dispose();
                        }
                        break;
                    }
                case "Get": // only OnBattery is asked for
                    {
                        var writer = context.CreateReplyWriter("v");
                        try
                        {
                            writer.WriteVariantBool(OnBattery);
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
    }

    private static void EmitPropertiesChanged(DBusConnection bus, string path, string iface) =>
        bus.EmitSignal(path, "org.freedesktop.DBus.Properties", "PropertiesChanged", "sa{sv}as", (ref MessageWriter w) =>
        {
            w.WriteString(iface);
            w.WriteDictionaryEnd(w.WriteDictionaryStart());
            w.WriteArrayEnd(w.WriteArrayStart(DBusType.String));
        });

    [Fact]
    public void Reads_devices_and_follows_changes()
    {
        using var bus = new DBusDaemon();
        using var app = sway.Connect();

        var upowerBus = app.Complete(OopsDBus.ConnectAsync(app, bus.Address));
        var upower = new FakeUPower(app) { Devices = { MousePath } };
        var display = new FakeDevice(app, DisplayPath, new() { ["Type"] = 0u, ["IsPresent"] = false, ["Percentage"] = 0.0 });
        var mouse = new FakeDevice(app, MousePath, new()
        {
            ["Type"] = 5u,
            ["Model"] = "G602",
            ["IsPresent"] = true,
            ["IsRechargeable"] = true,
            ["PowerSupply"] = false,
            ["State"] = 2u,
            ["Percentage"] = 55.0,
            ["BatteryLevel"] = 6u,
            ["WarningLevel"] = 1u,
            ["IconName"] = "battery-low-symbolic",
            ["TimeToEmpty"] = 0L,
        });
        upowerBus.AddMethodHandler(upower);
        upowerBus.AddMethodHandler(display);
        upowerBus.AddMethodHandler(mouse);
        Assert.True(app.Complete(upowerBus.RequestNameOrThrowAsync(PowerService.BusName).ContinueWith(t => t.Exception is null)));

        var shellBus = app.Complete(OopsDBus.ConnectAsync(app, bus.Address));
        using var power = app.Complete(PowerService.StartAsync(app, shellBus));

        // A level-only mouse; a desktop without a system battery.
        var m = Assert.Single(power.Devices);
        Assert.Equal(DeviceKind.Mouse, m.Kind);
        Assert.Equal("G602", m.Model);
        Assert.Equal(BatteryState.Discharging, m.State);
        Assert.False(m.HasExactPercentage);
        Assert.Equal("Normal", m.Describe());
        Assert.True(m.HasBattery);
        Assert.Null(m.TimeToEmpty);
        Assert.False(power.DisplayDevice!.HasBattery);
        Assert.False(power.OnBattery);

        // The level drops.
        int changes = 0;
        m.Changed += () => changes++;
        mouse.Properties["BatteryLevel"] = 3u;
        EmitPropertiesChanged(upowerBus, MousePath, PowerService.DeviceInterface);
        app.RunUntil(() => m.Level == BatteryLevel.Low, what: "level change");
        Assert.Equal(1, changes);

        // A periodic re-announcement without changes does not raise Changed.
        int reads = mouse.Reads;
        EmitPropertiesChanged(upowerBus, MousePath, PowerService.DeviceInterface);
        app.RunUntil(() => mouse.Reads > reads, what: "unchanged re-read");
        app.RunFor(100);
        mouse.Properties["BatteryLevel"] = 4u;
        EmitPropertiesChanged(upowerBus, MousePath, PowerService.DeviceInterface);
        app.RunUntil(() => m.Level == BatteryLevel.Critical, what: "second level change");
        Assert.Equal(2, changes);

        // A laptop battery appears, with an exact percentage and an estimate.
        var laptop = new FakeDevice(app, LaptopPath, new()
        {
            ["Type"] = 2u,
            ["IsPresent"] = true,
            ["PowerSupply"] = true,
            ["State"] = 2u,
            ["Percentage"] = 80.0,
            ["BatteryLevel"] = 1u,
            ["TimeToEmpty"] = 3600L,
        });
        upowerBus.AddMethodHandler(laptop);
        upower.Devices.Add(LaptopPath);
        upowerBus.EmitSignal(PowerService.ObjectPath, PowerService.Interface, "DeviceAdded", "o", (ref MessageWriter w) => w.WriteObjectPath(LaptopPath));
        app.RunUntil(() => power.Devices.Count == 2, what: "device added");
        var l = power.Devices[1];
        Assert.True(l.HasExactPercentage);
        Assert.Equal("80%", l.Describe());
        Assert.Equal(TimeSpan.FromHours(1), l.TimeToEmpty);

        // Unplugged.
        upower.OnBattery = true;
        EmitPropertiesChanged(upowerBus, PowerService.ObjectPath, PowerService.Interface);
        app.RunUntil(() => power.OnBattery, what: "on battery");

        // The mouse is switched off.
        upower.Devices.Remove(MousePath);
        upowerBus.EmitSignal(PowerService.ObjectPath, PowerService.Interface, "DeviceRemoved", "o", (ref MessageWriter w) => w.WriteObjectPath(MousePath));
        app.RunUntil(() => power.Devices.Count == 1, what: "device removed");
        Assert.Same(l, power.Devices[0]);
    }
}
