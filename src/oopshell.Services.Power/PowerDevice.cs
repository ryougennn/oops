using Tmds.DBus.Protocol;

namespace oopshell.Services.Power;

/// <summary>UPower device type (<c>Type</c>).</summary>
public enum DeviceKind
{
    Unknown, LinePower, Battery, Ups, Monitor, Mouse, Keyboard, Pda, Phone, MediaPlayer, Tablet, Computer,
    GamingInput, Pen, Touchpad, Modem, Network, Headset, Speakers, Headphones, Video, OtherAudio, RemoteControl,
    Printer, Scanner, Camera, Wearable, Toy, BluetoothGeneric,
}

/// <summary>UPower charge state (<c>State</c>).</summary>
public enum BatteryState
{
    Unknown, Charging, Discharging, Empty, FullyCharged, PendingCharge, PendingDischarge,
}

/// <summary>
/// UPower coarse battery level (<c>BatteryLevel</c>). Devices that only report a level (many mice and keyboards)
/// have a meaningless <see cref="PowerDevice.Percentage"/>; <see cref="None"/> means the percentage is exact.
/// </summary>
public enum BatteryLevel
{
    Unknown = 0,
    None = 1,
    Low = 3,
    Critical = 4,
    Normal = 6,
    High = 7,
    Full = 8,
}

/// <summary>UPower warning level (<c>WarningLevel</c>).</summary>
public enum WarningLevel
{
    Unknown, None, Discharging, Low, Critical, Action,
}

/// <summary>
/// One UPower device (<c>org.freedesktop.UPower.Device</c>): a laptop battery, an AC adapter, a UPS, or a
/// peripheral such as a wireless mouse. Properties refresh when UPower signals a change.
/// </summary>
public sealed class PowerDevice
{
    internal PowerDevice(string path) => Path = path;

    /// <summary>D-Bus object path, e.g. <c>/org/freedesktop/UPower/devices/mouse_hidpp_battery_0</c>.</summary>
    public string Path { get; }
    public DeviceKind Kind { get; private set; }
    public string Model { get; private set; } = "";
    public string Vendor { get; private set; } = "";
    public string NativePath { get; private set; } = "";
    /// <summary>True if the device powers the computer (a laptop battery), false for peripherals.</summary>
    public bool PowerSupply { get; private set; }
    public bool IsPresent { get; private set; }
    public bool IsRechargeable { get; private set; }
    public BatteryState State { get; private set; }
    /// <summary>0–100. Only meaningful when <see cref="HasExactPercentage"/>.</summary>
    public double Percentage { get; private set; }
    public BatteryLevel Level { get; private set; }
    public WarningLevel WarningLevel { get; private set; }
    /// <summary>A freedesktop icon name such as <c>battery-good-charging-symbolic</c>.</summary>
    public string IconName { get; private set; } = "";
    /// <summary>Estimated time until empty, or null when unknown or not discharging.</summary>
    public TimeSpan? TimeToEmpty { get; private set; }
    /// <summary>Estimated time until full, or null when unknown or not charging.</summary>
    public TimeSpan? TimeToFull { get; private set; }

    /// <summary>False for devices that only report a coarse <see cref="Level"/>.</summary>
    public bool HasExactPercentage => Level == BatteryLevel.None;

    /// <summary>True for anything with a battery (not AC adapters, and not an empty display device).</summary>
    public bool HasBattery => IsPresent && Kind is not (DeviceKind.Unknown or DeviceKind.LinePower);

    /// <summary>
    /// A property changed (on the UI thread). UPower re-announces devices periodically without changes; those
    /// updates do not raise this.
    /// </summary>
    public event Action? Changed;

    /// <summary>"55%", or the level ("Normal") for devices without an exact percentage.</summary>
    public string Describe() => HasExactPercentage ? $"{Percentage:0}%" : Level.ToString();

    internal void Apply(Dictionary<string, VariantValue> p)
    {
        var before = Snapshot();
        Kind = (DeviceKind)UInt("Type");
        Model = Str("Model");
        Vendor = Str("Vendor");
        NativePath = Str("NativePath");
        PowerSupply = Bool("PowerSupply");
        IsPresent = Bool("IsPresent");
        IsRechargeable = Bool("IsRechargeable");
        State = (BatteryState)UInt("State");
        Percentage = p.TryGetValue("Percentage", out var pct) && pct.Type == VariantValueType.Double ? pct.GetDouble() : 0;
        Level = (BatteryLevel)UInt("BatteryLevel");
        WarningLevel = (WarningLevel)UInt("WarningLevel");
        IconName = Str("IconName");
        TimeToEmpty = Seconds("TimeToEmpty");
        TimeToFull = Seconds("TimeToFull");
        if (Snapshot() != before)
            Changed?.Invoke();

        string Str(string key) => p.TryGetValue(key, out var v) && v.Type == VariantValueType.String ? v.GetString() : "";
        bool Bool(string key) => p.TryGetValue(key, out var v) && v.Type == VariantValueType.Bool && v.GetBool();
        uint UInt(string key) => p.TryGetValue(key, out var v) && v.Type == VariantValueType.UInt32 ? v.GetUInt32() : 0;
        TimeSpan? Seconds(string key) => p.TryGetValue(key, out var v) && v.Type == VariantValueType.Int64 && v.GetInt64() > 0
            ? TimeSpan.FromSeconds(v.GetInt64()) : null;
    }

    private (DeviceKind, string, string, string, bool, bool, bool, BatteryState, double, BatteryLevel, WarningLevel, string, TimeSpan?, TimeSpan?) Snapshot() =>
        (Kind, Model, Vendor, NativePath, PowerSupply, IsPresent, IsRechargeable, State, Percentage, Level, WarningLevel, IconName, TimeToEmpty, TimeToFull);

    public override string ToString() => $"{Kind} {Model} {Describe()} {State}";
}
