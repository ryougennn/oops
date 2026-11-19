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
    private const string AdapterInterface = "org.bluez.Adapter1";
    private const string DeviceInterface = "org.bluez.Device1";
    private const string BatteryInterface = "org.bluez.Battery1";
    private const string AgentManagerPath = "/org/bluez";
    private const string AgentManagerInterface = "org.bluez.AgentManager1";

    private readonly OopsApp _app;
    private readonly DBusConnection _connection;
    private readonly ObservableCollection<BluetoothAdapter> _adapters = new();
    private readonly ObservableCollection<BluetoothDevice> _devices = new();
    private readonly List<IDisposable> _watches = new();
    private PairingAgent? _agent;
    private IDisposable? _refreshTimer;
    private bool _refreshing, _refreshAgain, _disposed;

    private BluetoothService(OopsApp app, DBusConnection connection)
    {
        _app = app;
        _connection = connection;
        Adapters = new ReadOnlyObservableCollection<BluetoothAdapter>(_adapters);
        Devices = new ReadOnlyObservableCollection<BluetoothDevice>(_devices);
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
            service._watches.Add(await connection.WatchSignalAsync(app, BusName, null, iface, member, (_, _) => true, _ => service.ScheduleRefresh()));
        }
        await service.RefreshAsync();
        return service;
    }

    public ReadOnlyObservableCollection<BluetoothAdapter> Adapters { get; }

    /// <summary>The default (first) adapter, if any.</summary>
    public BluetoothAdapter? Adapter => _adapters.FirstOrDefault();

    /// <summary>Known devices: connected first, then paired, then by name.</summary>
    public ReadOnlyObservableCollection<BluetoothDevice> Devices { get; }

    /// <summary>Something changed (UI thread).</summary>
    public event Action? Changed;

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
        if (_agent is not null)
            return;
        var created = new PairingAgent(_app, this, await _connection.GetNameOwnerAsync(BusName));
        _connection.AddMethodHandler(created);
        try
        {
            await _connection.CallAsync(BusName, AgentManagerPath, AgentManagerInterface, "RegisterAgent", (ref MessageWriter w) =>
            {
                w.WriteObjectPath(created.Path);
                w.WriteString("KeyboardDisplay");
            }, "os");
            await _connection.CallAsync(BusName, AgentManagerPath, AgentManagerInterface, "RequestDefaultAgent",
                (ref MessageWriter w) => w.WriteObjectPath(created.Path), "o");
        }
        catch
        {
            _connection.RemoveMethodHandler(created.Path);
            throw;
        }
        _agent = created;
    }

    private void ScheduleRefresh()
    {
        if (_disposed)
            return;
        _refreshTimer?.Dispose();
        _refreshTimer = _app.SetTimer(TimeSpan.FromMilliseconds(100), null, () =>
        {
            _refreshTimer = null;
            _ = RefreshQuietlyAsync();
        });
    }

    /// <summary>A refresh nobody awaits: errors are dropped, and the next signal from the service retries.</summary>
    private async Task RefreshQuietlyAsync()
    {
        try
        {
            await RefreshAsync();
        }
        catch (Exception e) when (e is DBusErrorReplyException or DBusConnectionClosedException or DBusConnectionException)
        {
        }
    }

    private async Task RefreshAsync()
    {
        if (_refreshing)
        {
            _refreshAgain = true;
            return;
        }
        _refreshing = true;
        try
        {
            do
            {
                _refreshAgain = false;
                Apply(await _connection.CallAsync(BusName, "/", "org.freedesktop.DBus.ObjectManager", "GetManagedObjects", (m, _) => ReadObjects(m)));
            }
            while (_refreshAgain && !_disposed);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private static Dictionary<string, Dictionary<string, Dictionary<string, VariantValue>>> ReadObjects(Message message)
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

    private void Apply(Dictionary<string, Dictionary<string, Dictionary<string, VariantValue>>> objects)
    {
        var seenAdapters = new List<BluetoothAdapter>();
        var seenDevices = new List<BluetoothDevice>();
        foreach (var (path, interfaces) in objects.OrderBy(o => o.Key, StringComparer.Ordinal))
        {
            if (interfaces.TryGetValue(AdapterInterface, out var a))
            {
                var adapter = _adapters.FirstOrDefault(x => x.Path == path) ?? new BluetoothAdapter(path);
                adapter.Name = Str(a, "Alias") ?? Str(a, "Name") ?? "";
                adapter.Address = Str(a, "Address") ?? "";
                adapter.Powered = Bool(a, "Powered");
                adapter.Discovering = Bool(a, "Discovering");
                adapter.Discoverable = Bool(a, "Discoverable");
                seenAdapters.Add(adapter);
            }
            if (interfaces.TryGetValue(DeviceInterface, out var d))
            {
                var device = _devices.FirstOrDefault(x => x.Path == path) ?? new BluetoothDevice(path);
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
        Sync(_adapters, seenAdapters);
        Sync(_devices, seenDevices.OrderByDescending(d => d.Connected).ThenByDescending(d => d.Paired)
            .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
        if (!_disposed)
            Changed?.Invoke();
    }

    /// <summary>Makes <paramref name="target"/> equal <paramref name="wanted"/> with minimal moves (keeps UI selections stable).</summary>
    private static void Sync<T>(ObservableCollection<T> target, List<T> wanted) where T : class
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

    private static string? Str(Dictionary<string, VariantValue> props, string name) =>
        props.TryGetValue(name, out var v) && v.AsString() is { Length: > 0 } s ? s.Clip(256) : null;

    private static bool Bool(Dictionary<string, VariantValue> props, string name) => props.TryGetValue(name, out var v) && v.AsBool() == true;

    private Task SetAsync(string path, string iface, string property, bool value) =>
        _connection.CallAsync(BusName, path, "org.freedesktop.DBus.Properties", "Set", (ref MessageWriter w) =>
        {
            w.WriteString(iface);
            w.WriteString(property);
            w.WriteVariantBool(value);
        }, "ssv");

    /// <summary>Turns the adapter's radio on or off.</summary>
    public Task SetPoweredAsync(BluetoothAdapter adapter, bool powered) => SetAsync(adapter.Path, AdapterInterface, "Powered", powered);

    /// <summary>Starts scanning; found devices appear in <see cref="Devices"/>. Stop when the user is done (it costs power).</summary>
    public Task StartDiscoveryAsync(BluetoothAdapter adapter) => _connection.CallAsync(BusName, adapter.Path, AdapterInterface, "StartDiscovery");

    public Task StopDiscoveryAsync(BluetoothAdapter adapter) => _connection.CallAsync(BusName, adapter.Path, AdapterInterface, "StopDiscovery");

    /// <summary>Connects (pairing first is the caller's job: see <see cref="PairAsync"/>).</summary>
    public Task ConnectAsync(BluetoothDevice device) => _connection.CallAsync(BusName, device.Path, DeviceInterface, "Connect");

    public Task DisconnectAsync(BluetoothDevice device) => _connection.CallAsync(BusName, device.Path, DeviceInterface, "Disconnect");

    /// <summary>Pairs and trusts the device (so it may reconnect by itself later).</summary>
    public async Task PairAsync(BluetoothDevice device)
    {
        try
        {
            await _connection.CallAsync(BusName, device.Path, DeviceInterface, "Pair");
        }
        finally
        {
            // BlueZ cancels code displays only when pairing fails; end them on success too.
            _agent?.EndDisplays(device.Path);
        }
        await SetTrustedAsync(device, true);
    }

    public Task SetTrustedAsync(BluetoothDevice device, bool trusted) => SetAsync(device.Path, DeviceInterface, "Trusted", trusted);

    /// <summary>Removes the device and its pairing.</summary>
    public Task ForgetAsync(BluetoothDevice device) =>
        _connection.CallAsync(BusName, device.AdapterPath, AdapterInterface, "RemoveDevice", (ref MessageWriter w) => w.WriteObjectPath(device.Path), "o");

    public void Dispose()
    {
        _disposed = true;
        if (_agent is not null)
        {
            _agent.CancelAll();
            _connection.RemoveMethodHandler(_agent.Path);
            _ = _connection.CallAsync(BusName, AgentManagerPath, AgentManagerInterface, "UnregisterAgent",
                (ref MessageWriter w) => w.WriteObjectPath(_agent.Path), "o").ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        }
        _refreshTimer?.Dispose();
        foreach (var watch in _watches)
            watch.Dispose();
        _watches.Clear();
    }
}
