using System.Collections.ObjectModel;
using oopshell.Services.DBus;
using Tmds.DBus.Protocol;

namespace oopshell.Services.Bluetooth;

/// <summary>A Bluetooth controller. Updated in place; <see cref="BluetoothService.Changed"/> fires after changes.</summary>
public sealed class BluetoothAdapter
{
    internal BluetoothAdapter(string path) => Path = path;

    public string Path { get; }
    /// <summary>The name other devices see (BlueZ's alias).</summary>
    public string Name { get; internal set; } = "";
    public string Address { get; internal set; } = "";
    public bool Powered { get; internal set; }
    /// <summary>Scanning for devices (see <see cref="BluetoothService.StartDiscoveryAsync"/>).</summary>
    public bool Discovering { get; internal set; }
    public bool Discoverable { get; internal set; }

    public override string ToString() => $"{Name} ({Address})";
}

/// <summary>A device BlueZ knows: paired, or seen while discovering. Updated in place.</summary>
public sealed class BluetoothDevice
{
    internal BluetoothDevice(string path) => Path = path;

    public string Path { get; }
    /// <summary>The device's name (or the user's alias for it); the address when it has none.</summary>
    public string Name { get; internal set; } = "";
    public string Address { get; internal set; } = "";
    /// <summary>A freedesktop icon name BlueZ derives from the device class, e.g. <c>audio-headset</c>, <c>input-mouse</c>.</summary>
    public string? Icon { get; internal set; }
    public bool Paired { get; internal set; }
    public bool Trusted { get; internal set; }
    public bool Connected { get; internal set; }
    /// <summary>Battery percentage, for devices that report it (BlueZ's Battery1).</summary>
    public int? Battery { get; internal set; }
    /// <summary>Signal strength in dBm while discovering, if known.</summary>
    public int? Rssi { get; internal set; }
    public string AdapterPath { get; internal set; } = "";

    public override string ToString() => $"{Name} ({Address}){(Connected ? " connected" : Paired ? " paired" : "")}";
}

/// <summary>
/// Bluetooth from BlueZ (<c>org.bluez</c>, system bus): adapters and devices, kept up to date.
/// <code>
/// var bus = await OopsDBus.ConnectAsync(app, DBusAddress.System);
/// using var bluetooth = await BluetoothService.StartAsync(app, bus);
/// bluetooth.Changed += panel.Invalidate;                              // UI thread
/// await bluetooth.ConnectAsync(bluetooth.Devices.First(d => d.Paired));
/// </code>
/// </summary>
/// <remarks>
/// Devices that pair with a PIN, a passkey or a confirmation need a pairing agent: call
/// <see cref="RegisterPairingAgentAsync"/> and answer <see cref="PairingRequested"/>.
/// </remarks>
public sealed class BluetoothService : IDisposable
{
    public const string BusName = "org.bluez";
    const string AdapterInterface = "org.bluez.Adapter1";
    const string DeviceInterface = "org.bluez.Device1";
    const string BatteryInterface = "org.bluez.Battery1";

    readonly OopsApp app;
    readonly DBusConnection connection;
    readonly ObservableCollection<BluetoothAdapter> adapters = new();
    readonly ObservableCollection<BluetoothDevice> devices = new();
    readonly List<IDisposable> watches = new();
    IDisposable? refreshTimer;
    bool refreshing, refreshAgain, disposed;

    BluetoothService(OopsApp app, DBusConnection connection)
    {
        this.app = app;
        this.connection = connection;
        Adapters = new ReadOnlyObservableCollection<BluetoothAdapter>(adapters);
        Devices = new ReadOnlyObservableCollection<BluetoothDevice>(devices);
    }

    /// <summary>Reads BlueZ on <paramref name="connection"/> (the system bus). Throws if bluetoothd is not running.</summary>
    public static async Task<BluetoothService> StartAsync(OopsApp app, DBusConnection connection)
    {
        var service = new BluetoothService(app, connection);
        foreach (var (iface, member) in new[]
        {
            ("org.freedesktop.DBus.Properties", "PropertiesChanged"),
            ("org.freedesktop.DBus.ObjectManager", "InterfacesAdded"), ("org.freedesktop.DBus.ObjectManager", "InterfacesRemoved"),
        })
        {
            service.watches.Add(await connection.WatchSignalAsync(app, BusName, null, iface, member, (_, _) => true, _ => service.ScheduleRefresh()));
        }
        await service.RefreshAsync();
        return service;
    }

    public ReadOnlyObservableCollection<BluetoothAdapter> Adapters { get; }

    /// <summary>The default (first) adapter, if any.</summary>
    public BluetoothAdapter? Adapter => adapters.FirstOrDefault();

    /// <summary>Known devices: connected first, then paired, then by name.</summary>
    public ReadOnlyObservableCollection<BluetoothDevice> Devices { get; }

    /// <summary>Something changed (UI thread).</summary>
    public event Action? Changed;

    PairingAgent? agent;

    /// <summary>
    /// BlueZ asks the user something while pairing (a PIN, a passkey to compare, permission) (UI thread). Only
    /// raised after <see cref="RegisterPairingAgentAsync"/>.
    /// </summary>
    public event Action<BluetoothPairingRequest>? PairingRequested;

    internal bool HasPairingHandlers => PairingRequested is not null;

    internal void RaisePairingRequested(BluetoothPairingRequest request) => PairingRequested?.Invoke(request);

    /// <summary>
    /// Becomes BlueZ's default pairing agent (capability KeyboardDisplay: it can show codes and take typed ones),
    /// taking over from the desktop's agent while this service lives.
    /// </summary>
    public async Task RegisterPairingAgentAsync()
    {
        if (agent is not null)
            return;
        var created = new PairingAgent(app, this, await connection.GetNameOwnerAsync(BusName));
        connection.AddMethodHandler(created);
        try
        {
            await connection.CallAsync(BusName, "/org/bluez", "org.bluez.AgentManager1", "RegisterAgent", (ref MessageWriter w) =>
            {
                w.WriteObjectPath(created.Path);
                w.WriteString("KeyboardDisplay");
            }, "os");
            await connection.CallAsync(BusName, "/org/bluez", "org.bluez.AgentManager1", "RequestDefaultAgent",
                (ref MessageWriter w) => w.WriteObjectPath(created.Path), "o");
        }
        catch
        {
            connection.RemoveMethodHandler(created.Path);
            throw;
        }
        agent = created;
    }

    void ScheduleRefresh()
    {
        if (disposed)
            return;
        refreshTimer?.Dispose();
        refreshTimer = app.SetTimer(TimeSpan.FromMilliseconds(100), null, () =>
        {
            refreshTimer = null;
            _ = RefreshAsync();
        });
    }

    async Task RefreshAsync()
    {
        if (refreshing)
        {
            refreshAgain = true;
            return;
        }
        refreshing = true;
        try
        {
            do
            {
                refreshAgain = false;
                Apply(await connection.CallAsync(BusName, "/", "org.freedesktop.DBus.ObjectManager", "GetManagedObjects", (m, _) => ReadObjects(m)));
            }
            while (refreshAgain && !disposed);
        }
        finally
        {
            refreshing = false;
        }
    }

    static Dictionary<string, Dictionary<string, Dictionary<string, VariantValue>>> ReadObjects(Message message)
    {
        var reader = message.GetBodyReader();
        var objects = new Dictionary<string, Dictionary<string, Dictionary<string, VariantValue>>>();
        var end = reader.ReadDictionaryStart();
        while (reader.HasNext(end))
        {
            string path = reader.ReadObjectPathAsString();
            var interfaces = new Dictionary<string, Dictionary<string, VariantValue>>();
            var interfacesEnd = reader.ReadDictionaryStart();
            while (reader.HasNext(interfacesEnd))
                interfaces[reader.ReadString()] = reader.ReadDictionaryOfStringToVariantValue();
            objects[path] = interfaces;
        }
        return objects;
    }

    void Apply(Dictionary<string, Dictionary<string, Dictionary<string, VariantValue>>> objects)
    {
        var seenAdapters = new List<BluetoothAdapter>();
        var seenDevices = new List<BluetoothDevice>();
        foreach (var (path, interfaces) in objects.OrderBy(o => o.Key, StringComparer.Ordinal))
        {
            if (interfaces.TryGetValue(AdapterInterface, out var a))
            {
                var adapter = adapters.FirstOrDefault(x => x.Path == path) ?? new BluetoothAdapter(path);
                adapter.Name = Str(a, "Alias") ?? Str(a, "Name") ?? "";
                adapter.Address = Str(a, "Address") ?? "";
                adapter.Powered = Bool(a, "Powered");
                adapter.Discovering = Bool(a, "Discovering");
                adapter.Discoverable = Bool(a, "Discoverable");
                seenAdapters.Add(adapter);
            }
            if (interfaces.TryGetValue(DeviceInterface, out var d))
            {
                var device = devices.FirstOrDefault(x => x.Path == path) ?? new BluetoothDevice(path);
                device.Address = Str(d, "Address") ?? "";
                device.Name = Str(d, "Alias") ?? Str(d, "Name") ?? device.Address;
                device.Icon = Str(d, "Icon");
                device.Paired = Bool(d, "Paired");
                device.Trusted = Bool(d, "Trusted");
                device.Connected = Bool(d, "Connected");
                device.Rssi = d.TryGetValue("RSSI", out var rssi) ? rssi.AsInt32() : null;
                device.AdapterPath = d.TryGetValue("Adapter", out var ad) ? ad.AsString() ?? "" : "";
                device.Battery = interfaces.TryGetValue(BatteryInterface, out var b) && b.TryGetValue("Percentage", out var p) && p.AsInt64() is long pct and >= 0 and <= 100 ? (int)pct : null;
                seenDevices.Add(device);
            }
        }
        Sync(adapters, seenAdapters);
        Sync(devices, seenDevices.OrderByDescending(d => d.Connected).ThenByDescending(d => d.Paired)
            .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
        Changed?.Invoke();
    }

    /// <summary>Makes <paramref name="target"/> equal <paramref name="wanted"/> with minimal moves (keeps UI selections stable).</summary>
    static void Sync<T>(ObservableCollection<T> target, List<T> wanted) where T : class
    {
        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(target[i]))
                target.RemoveAt(i);
        }
        for (int i = 0; i < wanted.Count; i++)
        {
            int at = target.IndexOf(wanted[i]);
            if (at < 0)
                target.Insert(i, wanted[i]);
            else if (at != i)
                target.Move(at, i);
        }
    }

    static string? Str(Dictionary<string, VariantValue> props, string name) =>
        props.TryGetValue(name, out var v) && v.AsString() is { Length: > 0 } s ? s.Clip(256) : null;

    static bool Bool(Dictionary<string, VariantValue> props, string name) => props.TryGetValue(name, out var v) && v.AsBool() == true;

    Task SetAsync(string path, string iface, string property, bool value) =>
        connection.CallAsync(BusName, path, "org.freedesktop.DBus.Properties", "Set", (ref MessageWriter w) =>
        {
            w.WriteString(iface);
            w.WriteString(property);
            w.WriteVariantBool(value);
        }, "ssv");

    /// <summary>Turns the adapter's radio on or off.</summary>
    public Task SetPoweredAsync(BluetoothAdapter adapter, bool powered) => SetAsync(adapter.Path, AdapterInterface, "Powered", powered);

    /// <summary>Starts scanning; found devices appear in <see cref="Devices"/>. Stop when the user is done (it costs power).</summary>
    public Task StartDiscoveryAsync(BluetoothAdapter adapter) => connection.CallAsync(BusName, adapter.Path, AdapterInterface, "StartDiscovery");

    public Task StopDiscoveryAsync(BluetoothAdapter adapter) => connection.CallAsync(BusName, adapter.Path, AdapterInterface, "StopDiscovery");

    /// <summary>Connects (pairing first is the caller's job: see <see cref="PairAsync"/>).</summary>
    public Task ConnectAsync(BluetoothDevice device) => connection.CallAsync(BusName, device.Path, DeviceInterface, "Connect");

    public Task DisconnectAsync(BluetoothDevice device) => connection.CallAsync(BusName, device.Path, DeviceInterface, "Disconnect");

    /// <summary>Pairs and trusts the device (so it may reconnect by itself later).</summary>
    public async Task PairAsync(BluetoothDevice device)
    {
        await connection.CallAsync(BusName, device.Path, DeviceInterface, "Pair");
        await SetTrustedAsync(device, true);
    }

    public Task SetTrustedAsync(BluetoothDevice device, bool trusted) => SetAsync(device.Path, DeviceInterface, "Trusted", trusted);

    /// <summary>Removes the device and its pairing.</summary>
    public Task ForgetAsync(BluetoothDevice device) =>
        connection.CallAsync(BusName, device.AdapterPath, AdapterInterface, "RemoveDevice", (ref MessageWriter w) => w.WriteObjectPath(device.Path), "o");

    public void Dispose()
    {
        disposed = true;
        if (agent is not null)
        {
            agent.CancelAll();
            connection.RemoveMethodHandler(agent.Path);
            _ = connection.CallAsync(BusName, "/org/bluez", "org.bluez.AgentManager1", "UnregisterAgent",
                (ref MessageWriter w) => w.WriteObjectPath(agent.Path), "o").ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        }
        refreshTimer?.Dispose();
        foreach (var watch in watches)
            watch.Dispose();
        watches.Clear();
    }
}
