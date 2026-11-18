using System.Collections.ObjectModel;
using oopshell.Services.DBus;
using Tmds.DBus.Protocol;

namespace oopshell.Services.Power;

/// <summary>
/// Battery and power state from UPower (<c>org.freedesktop.UPower</c>, on the system bus). Read-only.
/// <code>
/// var bus = await OopsDBus.ConnectAsync(app, DBusAddress.System);
/// using var power = await PowerService.StartAsync(app, bus);
/// </code>
/// All state changes and events happen on the UI thread.
/// </summary>
public sealed class PowerService : IDisposable
{
    public const string BusName = "org.freedesktop.UPower";
    public const string ObjectPath = "/org/freedesktop/UPower";
    public const string Interface = "org.freedesktop.UPower";
    public const string DeviceInterface = "org.freedesktop.UPower.Device";

    readonly OopsApp app;
    readonly DBusConnection connection;
    readonly ObservableCollection<PowerDevice> devices = new();
    readonly List<IDisposable> watches = new();

    PowerService(OopsApp app, DBusConnection connection)
    {
        this.app = app;
        this.connection = connection;
        Devices = new ReadOnlyObservableCollection<PowerDevice>(devices);
    }

    /// <summary>Connects to UPower on <paramref name="connection"/> (normally the system bus). Throws if UPower is not running.</summary>
    public static async Task<PowerService> StartAsync(OopsApp app, DBusConnection connection)
    {
        var service = new PowerService(app, connection);
        await service.InitializeAsync();
        return service;
    }

    /// <summary>Every device except the display device, in UPower's order: batteries, AC adapters, peripherals.</summary>
    public ReadOnlyObservableCollection<PowerDevice> Devices { get; }

    /// <summary>
    /// UPower's composite of the batteries that power the computer: what a panel shows. On a desktop it has
    /// <see cref="PowerDevice.IsPresent"/> false.
    /// </summary>
    public PowerDevice? DisplayDevice { get; private set; }

    /// <summary>True while the computer runs on battery.</summary>
    public bool OnBattery { get; private set; }

    /// <summary><see cref="OnBattery"/> changed.</summary>
    public event Action? OnBatteryChanged;

    async Task InitializeAsync()
    {
        // Subscribe first so nothing that changes during the initial reads is missed.
        watches.Add(await connection.WatchSignalAsync(app, BusName, ObjectPath, Interface, "DeviceAdded",
            (message, _) => message.GetBodyReader().ReadObjectPathAsString(), path => _ = AddAsync(path)));
        watches.Add(await connection.WatchSignalAsync(app, BusName, ObjectPath, Interface, "DeviceRemoved",
            (message, _) => message.GetBodyReader().ReadObjectPathAsString(), Remove));
        watches.Add(await connection.WatchSignalAsync(app, BusName, null, "org.freedesktop.DBus.Properties", "PropertiesChanged",
            (message, _) => (Path: message.PathAsString ?? "", Interface: message.GetBodyReader().ReadString()), OnPropertiesChanged));

        await RefreshOnBatteryAsync();
        string displayPath = await connection.CallAsync(BusName, ObjectPath, Interface, "GetDisplayDevice",
            (message, _) => message.GetBodyReader().ReadObjectPathAsString());
        var display = new PowerDevice(displayPath);
        await RefreshAsync(display);
        DisplayDevice = display;

        var paths = await connection.CallAsync(BusName, ObjectPath, Interface, "EnumerateDevices",
            (message, _) => message.GetBodyReader().ReadArrayOfObjectPath());
        foreach (var path in paths)
            await AddAsync(path.ToString());
    }

    void OnPropertiesChanged((string Path, string Interface) change)
    {
        if (change.Interface == Interface && change.Path == ObjectPath)
            _ = RefreshOnBatteryAsync();
        else if (change.Interface == DeviceInterface && Find(change.Path) is { } device)
            _ = RefreshAsync(device);
    }

    PowerDevice? Find(string path) =>
        DisplayDevice?.Path == path ? DisplayDevice : devices.FirstOrDefault(d => d.Path == path);

    async Task AddAsync(string path)
    {
        if (Find(path) is not null)
            return;
        var device = new PowerDevice(path);
        if (!await RefreshAsync(device))
            return;
        await app.InvokeAsync(() =>
        {
            if (Find(path) is null)
                devices.Add(device);
            return Task.CompletedTask;
        });
    }

    void Remove(string path)
    {
        if (devices.FirstOrDefault(d => d.Path == path) is { } device)
            devices.Remove(device);
    }

    /// <summary>Re-reads all properties (PropertiesChanged is not always complete). False if the device is gone.</summary>
    async Task<bool> RefreshAsync(PowerDevice device)
    {
        Dictionary<string, VariantValue> properties;
        try
        {
            properties = await connection.GetAllPropertiesAsync(BusName, device.Path, DeviceInterface);
        }
        catch (DBusErrorReplyException)
        {
            return false; // removed meanwhile; DeviceRemoved follows
        }
        await app.InvokeAsync(() =>
        {
            device.Apply(properties);
            return Task.CompletedTask;
        });
        return true;
    }

    async Task RefreshOnBatteryAsync()
    {
        var value = await connection.GetPropertyAsync(BusName, ObjectPath, Interface, "OnBattery");
        await app.InvokeAsync(() =>
        {
            bool onBattery = value.Type == VariantValueType.Bool && value.GetBool();
            if (onBattery != OnBattery)
            {
                OnBattery = onBattery;
                OnBatteryChanged?.Invoke();
            }
            return Task.CompletedTask;
        });
    }

    public void Dispose()
    {
        foreach (var watch in watches)
            watch.Dispose();
        watches.Clear();
    }
}
