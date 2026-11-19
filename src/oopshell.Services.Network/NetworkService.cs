using System.Collections.ObjectModel;
using System.Text;
using oopshell.Services.DBus;
using Tmds.DBus.Protocol;

namespace oopshell.Services.Network;

/// <summary>Whether the internet is reachable, as NetworkManager's connectivity check sees it.</summary>
public enum NetworkConnectivity { Unknown, None, Portal, Limited, Full }

public enum NetworkDeviceType { Other, Ethernet, Wifi, Bluetooth, Modem, Loopback, WifiP2P, Bridge, Vpn }

public enum NetworkDeviceState { Unknown, Unmanaged, Unavailable, Disconnected, Connecting, Connected, Disconnecting, Failed }

/// <summary>A Wi-Fi network as one access point sees it (the strongest is listed per network name).</summary>
/// <param name="Path">NetworkManager's object path.</param>
/// <param name="Ssid">The network name.</param>
/// <param name="Strength">Signal strength, 0–100.</param>
/// <param name="Secured">Needs a password (WEP, WPA, enterprise).</param>
/// <param name="Frequency">MHz (2.4 GHz band below 3000).</param>
/// <param name="IsActive">The device is connected through it.</param>
public sealed record AccessPoint(string Path, string Ssid, int Strength, bool Secured, uint Frequency, bool IsActive);

/// <summary>A network interface. Updated in place; <see cref="NetworkService.Changed"/> fires after changes.</summary>
public sealed class NetworkDevice
{
    internal NetworkDevice(string path) => Path = path;

    public string Path { get; }
    /// <summary>e.g. <c>wlo1</c>, <c>enp42s0</c>.</summary>
    public string Interface { get; internal set; } = "";
    public NetworkDeviceType Type { get; internal set; }
    public NetworkDeviceState State { get; internal set; }
    /// <summary>The name of the connection in use (e.g. the Wi-Fi network or "Wired connection 1"), if any.</summary>
    public string? ConnectionName { get; internal set; }
    /// <summary>Wi-Fi networks in range, strongest first, one per name (Wi-Fi devices only).</summary>
    public IReadOnlyList<AccessPoint> AccessPoints { get; internal set; } = [];
    public AccessPoint? ActiveAccessPoint => AccessPoints.FirstOrDefault(a => a.IsActive);

    public override string ToString() => $"{Interface} ({Type}): {State}";
}

/// <summary>
/// Network state and Wi-Fi from NetworkManager (<c>org.freedesktop.NetworkManager</c>, system bus).
/// <code>
/// var bus = await OopsDBus.ConnectAsync(app, DBusAddress.System);
/// using var network = await NetworkService.StartAsync(app, bus);
/// network.Changed += bar.Invalidate;                       // UI thread
/// await network.ConnectAsync(network.Wifi!, accessPoint, password);
/// </code>
/// Changing things (Wi-Fi on/off, connecting) is subject to NetworkManager's polkit rules: allowed for the
/// user at the seat on typical desktops.
/// </summary>
public sealed class NetworkService : IDisposable
{
    public const string BusName = "org.freedesktop.NetworkManager";
    public const string ObjectPath = "/org/freedesktop/NetworkManager";
    private const string Manager = "org.freedesktop.NetworkManager";
    private const string DeviceInterface = "org.freedesktop.NetworkManager.Device";
    private const string WirelessInterface = "org.freedesktop.NetworkManager.Device.Wireless";
    private const string AccessPointInterface = "org.freedesktop.NetworkManager.AccessPoint";
    private const string ActiveInterface = "org.freedesktop.NetworkManager.Connection.Active";
    private const string SettingsPath = "/org/freedesktop/NetworkManager/Settings";
    private const string SettingsInterface = "org.freedesktop.NetworkManager.Settings";
    private const string ConnectionInterface = "org.freedesktop.NetworkManager.Settings.Connection";
    private const string AgentManagerPath = "/org/freedesktop/NetworkManager/AgentManager";
    private const string AgentManagerInterface = "org.freedesktop.NetworkManager.AgentManager";

    private readonly OopsApp _app;
    private readonly DBusConnection _connection;
    private readonly ObservableCollection<NetworkDevice> _devices = new();
    private readonly List<IDisposable> _watches = new();
    private SecretAgent? _secretAgent;
    private IDisposable? _refreshTimer;
    private bool _refreshing, _refreshAgain, _disposed;

    private NetworkService(OopsApp app, DBusConnection connection)
    {
        _app = app;
        _connection = connection;
        Devices = new ReadOnlyObservableCollection<NetworkDevice>(_devices);
    }

    /// <summary>Reads NetworkManager on <paramref name="connection"/> (the system bus). Throws if it is not running.</summary>
    public static async Task<NetworkService> StartAsync(OopsApp app, DBusConnection connection)
    {
        var service = new NetworkService(app, connection);
        // Anything NetworkManager changes triggers one (debounced) re-read: simpler and more robust than
        // tracking each property, and the object counts are small.
        foreach (var (iface, member) in new[]
        {
            ("org.freedesktop.DBus.Properties", "PropertiesChanged"), (Manager, "DeviceAdded"), (Manager, "DeviceRemoved"),
            (WirelessInterface, "AccessPointAdded"), (WirelessInterface, "AccessPointRemoved"),
        })
        {
            service._watches.Add(await connection.WatchSignalAsync(app, BusName, null, iface, member, (_, _) => true, _ => service.ScheduleRefresh()));
        }
        await service.RefreshAsync();
        return service;
    }

    /// <summary>Network interfaces except loopback, in NetworkManager's order.</summary>
    public ReadOnlyObservableCollection<NetworkDevice> Devices { get; }

    /// <summary>The first Wi-Fi device, if any.</summary>
    public NetworkDevice? Wifi => _devices.FirstOrDefault(d => d.Type == NetworkDeviceType.Wifi);

    public NetworkConnectivity Connectivity { get; private set; }
    public bool NetworkingEnabled { get; private set; }
    /// <summary>Wi-Fi is switched on in software (see <see cref="SetWifiEnabledAsync"/>).</summary>
    public bool WifiEnabled { get; private set; }
    /// <summary>False when a hardware switch (or airplane-mode key) turned the radio off.</summary>
    public bool WifiHardwareEnabled { get; private set; }
    /// <summary>The connection the default route uses (what a panel icon shows), if any.</summary>
    public string? PrimaryConnectionName { get; private set; }
    /// <summary>Its device type: Wifi, Ethernet…</summary>
    public NetworkDeviceType PrimaryConnectionType { get; private set; }

    /// <summary>Something above changed (UI thread).</summary>
    public event Action? Changed;

    /// <summary>
    /// NetworkManager needs a Wi-Fi password (UI thread): show a dialog and answer the request. Only raised after
    /// <see cref="RegisterSecretAgentAsync"/>.
    /// </summary>
    public event Action<NetworkSecretRequest>? SecretsRequested;

    internal bool HasSecretHandlers => SecretsRequested is not null;

    internal void RaiseSecretsRequested(NetworkSecretRequest request) => SecretsRequested?.Invoke(request);

    /// <summary>
    /// Becomes a NetworkManager secret agent for this user, so Wi-Fi passwords are asked for through
    /// <see cref="SecretsRequested"/> (a new network, or a saved password that stopped working). A desktop that
    /// already runs one (GNOME, Plasma) keeps working: NetworkManager asks each agent in turn.
    /// </summary>
    public async Task RegisterSecretAgentAsync(string identifier = "org.oopshell.SecretAgent")
    {
        if (_secretAgent is not null)
            return;
        var agent = new SecretAgent(_app, this, await _connection.GetNameOwnerAsync(BusName));
        _connection.AddMethodHandler(agent);
        try
        {
            await _connection.CallAsync(BusName, AgentManagerPath, AgentManagerInterface, "Register", (ref MessageWriter w) => w.WriteString(identifier), "s");
        }
        catch
        {
            _connection.RemoveMethodHandler(agent.Path);
            throw;
        }
        _secretAgent = agent;
    }

    private void ScheduleRefresh()
    {
        if (_disposed)
            return;
        _refreshTimer?.Dispose();
        _refreshTimer = _app.SetTimer(TimeSpan.FromMilliseconds(150), null, () =>
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
                await ReadAllAsync();
            }
            while (_refreshAgain && !_disposed);
        }
        catch (DBusErrorReplyException)
        {
            ScheduleRefresh(); // an object vanished mid-read; the signal for it is on its way
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task ReadAllAsync()
    {
        var manager = await _connection.GetAllPropertiesAsync(BusName, ObjectPath, Manager);
        string? primaryName = null;
        var primaryType = NetworkDeviceType.Other;
        if (Path(manager, "PrimaryConnection") is { } primary)
            (primaryName, primaryType) = await ActiveConnectionAsync(primary);

        var read = new List<(NetworkDevice Device, NetworkDeviceType Type, Dictionary<string, VariantValue> Props, string? Name, List<AccessPoint> AccessPoints)>();
        foreach (var path in manager.TryGetValue("Devices", out var list) ? list.AsObjectPaths() ?? [] : [])
        {
            var props = await _connection.GetAllPropertiesAsync(BusName, path, DeviceInterface);
            var type = DeviceType(props.TryGetValue("DeviceType", out var t) ? t.AsUInt32() ?? 0 : 0);
            if (type == NetworkDeviceType.Loopback)
                continue;
            string? name = Path(props, "ActiveConnection") is { } active ? (await ActiveConnectionAsync(active)).Name : null;
            var accessPoints = type == NetworkDeviceType.Wifi ? await AccessPointsAsync(path) : [];
            read.Add((_devices.FirstOrDefault(d => d.Path == path) ?? new NetworkDevice(path), type, props, name, accessPoints));
        }

        // Apply in one step; continuations resume on the UI thread.
        Connectivity = manager.TryGetValue("Connectivity", out var c) ? (NetworkConnectivity)Math.Min(c.AsUInt32() ?? 0, 4) : NetworkConnectivity.Unknown;
        NetworkingEnabled = Bool(manager, "NetworkingEnabled");
        WifiEnabled = Bool(manager, "WirelessEnabled");
        WifiHardwareEnabled = Bool(manager, "WirelessHardwareEnabled");
        PrimaryConnectionName = primaryName;
        PrimaryConnectionType = primaryType;
        foreach (var (device, type, props, name, accessPoints) in read)
        {
            device.Interface = props.TryGetValue("Interface", out var i) ? i.AsString() ?? "" : "";
            device.Type = type;
            device.State = DeviceState(props.TryGetValue("State", out var s) ? s.AsUInt32() ?? 0 : 0);
            device.ConnectionName = name;
            device.AccessPoints = accessPoints;
        }
        for (int i = _devices.Count - 1; i >= 0; i--)
        {
            if (!read.Any(r => r.Device == _devices[i]))
                _devices.RemoveAt(i);
        }
        foreach (var (device, _, _, _, _) in read)
        {
            if (!_devices.Contains(device))
                _devices.Add(device);
        }
        if (!_disposed)
            Changed?.Invoke();
    }

    private async Task<(string? Name, NetworkDeviceType Type)> ActiveConnectionAsync(string path)
    {
        var props = await _connection.GetAllPropertiesAsync(BusName, path, ActiveInterface);
        string? id = props.TryGetValue("Id", out var v) ? v.AsString() : null;
        var type = (props.TryGetValue("Type", out var t) ? t.AsString() : "") switch
        {
            "802-11-wireless" => NetworkDeviceType.Wifi,
            "802-3-ethernet" => NetworkDeviceType.Ethernet,
            "bluetooth" => NetworkDeviceType.Bluetooth,
            "gsm" or "cdma" => NetworkDeviceType.Modem,
            "vpn" or "wireguard" => NetworkDeviceType.Vpn,
            "bridge" => NetworkDeviceType.Bridge,
            _ => NetworkDeviceType.Other,
        };
        return (id, type);
    }

    private async Task<List<AccessPoint>> AccessPointsAsync(string devicePath)
    {
        var wireless = await _connection.GetAllPropertiesAsync(BusName, devicePath, WirelessInterface);
        string? active = Path(wireless, "ActiveAccessPoint");
        var all = new List<AccessPoint>();
        foreach (var path in wireless.TryGetValue("AccessPoints", out var list) ? list.AsObjectPaths() ?? [] : [])
        {
            var p = await _connection.GetAllPropertiesAsync(BusName, path, AccessPointInterface);
            string ssid = p.TryGetValue("Ssid", out var s) && s.AsBytes() is { } ssidBytes ? Encoding.UTF8.GetString(ssidBytes) : "";
            if (ssid.Length == 0)
                continue; // hidden network
            bool secured = (p.TryGetValue("Flags", out var f) ? (f.AsUInt32() ?? 0) & 1 : 0) != 0 // privacy
                || (p.TryGetValue("WpaFlags", out var wpa) ? wpa.AsUInt32() ?? 0 : 0) != 0
                || (p.TryGetValue("RsnFlags", out var rsn) ? rsn.AsUInt32() ?? 0 : 0) != 0;
            all.Add(new AccessPoint(path, ssid, p.TryGetValue("Strength", out var st) ? (int)Math.Clamp(st.AsInt64() ?? 0, 0, 100) : 0, secured,
                p.TryGetValue("Frequency", out var fr) ? fr.AsUInt32() ?? 0 : 0, path == active));
        }
        // One entry per network name: the one in use, else the strongest.
        return all.GroupBy(a => a.Ssid)
            .Select(g => g.FirstOrDefault(a => a.IsActive) ?? g.MaxBy(a => a.Strength)!)
            .OrderByDescending(a => a.IsActive).ThenByDescending(a => a.Strength).ToList();
    }

    private static string? Path(Dictionary<string, VariantValue> props, string name) =>
        props.TryGetValue(name, out var v) && v.AsString() is { } p && p != "/" ? p : null;

    private static bool Bool(Dictionary<string, VariantValue> props, string name) => props.TryGetValue(name, out var v) && v.AsBool() == true;

    private static NetworkDeviceType DeviceType(uint nm) => nm switch
    {
        1 => NetworkDeviceType.Ethernet,
        2 => NetworkDeviceType.Wifi,
        5 => NetworkDeviceType.Bluetooth,
        8 => NetworkDeviceType.Modem,
        13 => NetworkDeviceType.Bridge,
        29 or 16 => NetworkDeviceType.Vpn, // wireguard, tun
        30 => NetworkDeviceType.WifiP2P,
        32 => NetworkDeviceType.Loopback,
        _ => NetworkDeviceType.Other,
    };

    private static NetworkDeviceState DeviceState(uint nm) => nm switch
    {
        10 => NetworkDeviceState.Unmanaged,
        20 => NetworkDeviceState.Unavailable,
        30 => NetworkDeviceState.Disconnected,
        >= 40 and <= 90 => NetworkDeviceState.Connecting,
        100 => NetworkDeviceState.Connected,
        110 => NetworkDeviceState.Disconnecting,
        120 => NetworkDeviceState.Failed,
        _ => NetworkDeviceState.Unknown,
    };

    /// <summary>Switches Wi-Fi on or off (all Wi-Fi devices; NetworkManager remembers it).</summary>
    public Task SetWifiEnabledAsync(bool enabled) =>
        _connection.CallAsync(BusName, ObjectPath, "org.freedesktop.DBus.Properties", "Set", (ref MessageWriter w) =>
        {
            w.WriteString(Manager);
            w.WriteString("WirelessEnabled");
            w.WriteVariantBool(enabled);
        }, "ssv");

    /// <summary>Asks a Wi-Fi device to look for networks now (NetworkManager also scans by itself).</summary>
    public Task ScanAsync(NetworkDevice wifi) =>
        _connection.CallAsync(BusName, wifi.Path, WirelessInterface, "RequestScan",
            (ref MessageWriter w) => w.WriteDictionary(new Dictionary<string, VariantValue>()), "a{sv}");

    /// <summary>
    /// Connects <paramref name="wifi"/> to <paramref name="network"/>: with the saved connection for that name if
    /// there is one, else by creating one (a WPA/WPA2/WPA3-Personal network takes <paramref name="password"/>).
    /// Completes when NetworkManager starts connecting; watch the device's <see cref="NetworkDevice.State"/>.
    /// </summary>
    /// <remarks>
    /// When a saved network's password stopped working, NetworkManager asks a secret agent for a new one: call
    /// <see cref="RegisterSecretAgentAsync"/> and handle <see cref="SecretsRequested"/> to be that agent.
    /// </remarks>
    public async Task ConnectAsync(NetworkDevice wifi, AccessPoint network, string? password = null)
    {
        if (await SavedConnectionAsync(network.Ssid) is { } saved)
        {
            await _connection.CallAsync(BusName, ObjectPath, Manager, "ActivateConnection", (ref MessageWriter w) =>
            {
                w.WriteObjectPath(saved);
                w.WriteObjectPath(wifi.Path);
                w.WriteObjectPath(network.Path);
            }, "ooo");
            return;
        }
        var settings = new Dictionary<string, Dictionary<string, VariantValue>>
        {
            ["connection"] = new() { ["id"] = network.Ssid, ["type"] = "802-11-wireless" },
            ["802-11-wireless"] = new() { ["ssid"] = new Array<byte>(Encoding.UTF8.GetBytes(network.Ssid)) },
        };
        if (network.Secured && password is not null)
            settings["802-11-wireless-security"] = new() { ["key-mgmt"] = await KeyManagementAsync(network), ["psk"] = password };
        await _connection.CallAsync(BusName, ObjectPath, Manager, "AddAndActivateConnection", (ref MessageWriter w) =>
        {
            var dict = w.WriteDictionaryStart();
            foreach (var (group, values) in settings)
            {
                w.WriteDictionaryEntryStart();
                w.WriteString(group);
                w.WriteDictionary(values);
            }
            w.WriteDictionaryEnd(dict);
            w.WriteObjectPath(wifi.Path);
            w.WriteObjectPath(network.Path);
        }, "a{sa{sv}}oo");
    }

    /// <summary>"sae" for a WPA3-only network (RSN offers SAE but not PSK), else "wpa-psk" (WPA2, and WPA2/WPA3 mixed).</summary>
    private async Task<string> KeyManagementAsync(AccessPoint network)
    {
        const uint KeyMgmtPsk = 0x100, KeyMgmtSae = 0x400;
        uint rsn = (await _connection.GetPropertyAsync(BusName, network.Path, AccessPointInterface, "RsnFlags")).AsUInt32() ?? 0;
        return (rsn & KeyMgmtSae) != 0 && (rsn & KeyMgmtPsk) == 0 ? "sae" : "wpa-psk";
    }

    /// <summary>The saved Wi-Fi connection for a network name, if any.</summary>
    private async Task<string?> SavedConnectionAsync(string ssid)
    {
        var paths = await _connection.CallAsync(BusName, SettingsPath, SettingsInterface, "ListConnections",
            (m, _) => m.GetBodyReader().ReadArrayOfObjectPath());
        foreach (var path in paths.Select(p => p.ToString()))
        {
            var settings = await _connection.CallAsync(BusName, path, ConnectionInterface, "GetSettings", (m, _) =>
            {
                var reader = m.GetBodyReader();
                return SecretAgent.ReadSettings(ref reader);
            });
            if (settings.TryGetValue("802-11-wireless", out var wireless) && wireless.TryGetValue("ssid", out var bytes)
                && bytes.AsBytes() is { } saved && Encoding.UTF8.GetString(saved) == ssid)
                return path;
        }
        return null;
    }

    /// <summary>Disconnects a device (it stays down until something connects it again).</summary>
    public Task DisconnectAsync(NetworkDevice device) => _connection.CallAsync(BusName, device.Path, DeviceInterface, "Disconnect");

    public void Dispose()
    {
        _disposed = true;
        if (_secretAgent is not null)
        {
            _secretAgent.CancelAll();
            _connection.RemoveMethodHandler(_secretAgent.Path);
            _ = _connection.CallAsync(BusName, AgentManagerPath, AgentManagerInterface, "Unregister")
                .ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        }
        _refreshTimer?.Dispose();
        foreach (var watch in _watches)
            watch.Dispose();
        _watches.Clear();
    }
}
