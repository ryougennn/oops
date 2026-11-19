using oopshell.Services.DBus;
using Tmds.DBus.Protocol;

namespace oopshell.Services.Tray;

/// <summary>A tray icon image: 1..512 pixels per side, exactly Width × Height × 4 bytes of ARGB32 in network byte order.</summary>
public sealed record TrayPixmap(int Width, int Height, byte[] Argb);

/// <summary>A tray icon (<c>org.kde.StatusNotifierItem</c>). Properties refresh when the item signals changes.</summary>
public sealed class TrayItem
{
    private const string Interface = "org.kde.StatusNotifierItem";
    private readonly DBusConnection _connection;
    private readonly OopsApp _app;
    private readonly List<IDisposable> _watches = new();

    internal TrayItem(OopsApp app, DBusConnection connection, string service)
    {
        _app = app;
        _connection = connection;
        Service = service;
        int slash = service.IndexOf('/');
        BusName = slash < 0 ? service : service[..slash];
        ObjectPath = slash < 0 ? "/StatusNotifierItem" : service[slash..];
    }

    /// <summary>The registration string: <c>busname</c> or <c>busname/object/path</c>.</summary>
    public string Service { get; }
    public string BusName { get; }
    public string ObjectPath { get; }

    public string Id { get; private set; } = "";
    public string Title { get; private set; } = "";
    /// <summary>ApplicationStatus, Communications, SystemServices or Hardware.</summary>
    public string Category { get; private set; } = "";
    /// <summary>Passive, Active or NeedsAttention.</summary>
    public string Status { get; private set; } = "Active";
    public string? IconName { get; private set; }
    /// <summary>Extra icon theme directory the item ships its icons in.</summary>
    public string? IconThemePath { get; private set; }
    public IReadOnlyList<TrayPixmap> IconPixmaps { get; private set; } = [];
    public string? AttentionIconName { get; private set; }
    public string ToolTipTitle { get; private set; } = "";
    public string ToolTipText { get; private set; } = "";
    /// <summary>The item's dbusmenu object path, if it has a menu.</summary>
    public string? MenuPath { get; private set; }
    /// <summary>The item only has a menu: left click should open it instead of calling Activate.</summary>
    public bool ItemIsMenu { get; private set; }

    /// <summary>Properties were refreshed.</summary>
    public event Action? Changed;

    internal async Task StartAsync()
    {
        await RefreshAsync();
        foreach (var signal in new[] { "NewTitle", "NewIcon", "NewAttentionIcon", "NewOverlayIcon", "NewToolTip", "NewStatus", "NewMenu" })
            _watches.Add(await _connection.WatchSignalAsync(_app, BusName, ObjectPath, Interface, signal, (_, _) => true, OnChangeSignal));
    }

    // Fire and forget: a failed refresh keeps the previous state.
    private void OnChangeSignal(bool signal) => _ = RefreshAsync();

    /// <summary>Re-reads every property (items signal changes without saying what changed).</summary>
    public async Task RefreshAsync()
    {
        Dictionary<string, VariantValue> properties;
        try
        {
            properties = await _connection.GetAllPropertiesAsync(BusName, ObjectPath, Interface);
        }
        catch (DBusErrorReplyException)
        {
            return; // The item is gone; the host removes it when its name vanishes.
        }
        await _app.InvokeAsync(() =>
        {
            Apply(properties);
            Changed?.Invoke();
            return Task.CompletedTask;
        });
    }

    private void Apply(Dictionary<string, VariantValue> p)
    {
        string? Str(string key) => p.TryGetValue(key, out var v) && v.Type == VariantValueType.String ? v.GetString() : null;
        Id = Str("Id") ?? "";
        Title = Str("Title") ?? "";
        Category = Str("Category") ?? "";
        Status = Str("Status") ?? "Active";
        IconName = Str("IconName") is { Length: > 0 } icon ? icon : null;
        IconThemePath = Str("IconThemePath") is { Length: > 0 } themePath ? themePath : null;
        AttentionIconName = Str("AttentionIconName") is { Length: > 0 } attention ? attention : null;
        IconPixmaps = p.TryGetValue("IconPixmap", out var pixmaps) ? ReadPixmaps(pixmaps) : [];
        if (p.TryGetValue("ToolTip", out var tip))
        {
            ToolTipTitle = tip.Field(2)?.AsString()?.Clip(1024) ?? "";
            ToolTipText = tip.Field(3)?.AsString()?.Clip(4096) ?? "";
        }
        MenuPath = p.TryGetValue("Menu", out var menu) && menu.Type == VariantValueType.ObjectPath ? menu.GetObjectPathAsString() : null;
        ItemIsMenu = p.TryGetValue("ItemIsMenu", out var isMenu) && isMenu.Type == VariantValueType.Bool && isMenu.GetBool();
    }

    /// <summary>Largest tray pixmap accepted, per side.</summary>
    public const int MaxPixmapSide = 512;

    /// <summary>The a(iiay) icon list; entries that are malformed, oversized or whose data does not match width × height × 4 are dropped.</summary>
    private static List<TrayPixmap> ReadPixmaps(VariantValue value)
    {
        var result = new List<TrayPixmap>();
        if (value.Type != VariantValueType.Array)
            return result;
        for (int i = 0; i < Math.Min(value.Count, 16); i++)
        {
            var item = value.GetItem(i);
            if (item.Field(0)?.AsInt32() is int w and > 0 and <= MaxPixmapSide && item.Field(1)?.AsInt32() is int h and > 0 and <= MaxPixmapSide
                && item.Field(2)?.AsBytes() is { } argb && argb.Length == w * h * 4)
                result.Add(new TrayPixmap(w, h, argb));
        }
        return result;
    }

    /// <summary>The pixmap closest to <paramref name="size"/> pixels (preferring larger), or null.</summary>
    public TrayPixmap? BestPixmap(int size) =>
        IconPixmaps.OrderBy(p => p.Width >= size ? p.Width - size : 1000 + size - p.Width).FirstOrDefault();

    /// <summary>Primary action (usually left click). Coordinates are screen positions, often ignored.</summary>
    public Task ActivateAsync(int x = 0, int y = 0) => CallXY("Activate", x, y);

    /// <summary>Secondary action (usually middle click).</summary>
    public Task SecondaryActivateAsync(int x = 0, int y = 0) => CallXY("SecondaryActivate", x, y);

    /// <summary>Asks the item to show its own context menu (for items without a dbusmenu).</summary>
    public Task ContextMenuAsync(int x = 0, int y = 0) => CallXY("ContextMenu", x, y);

    public Task ScrollAsync(int delta, bool horizontal = false) =>
        _connection.CallAsync(BusName, ObjectPath, Interface, "Scroll", (ref MessageWriter w) =>
        {
            w.WriteInt32(delta);
            w.WriteString(horizontal ? "horizontal" : "vertical");
        }, "is");

    /// <summary>The item's menu, or null if it has none.</summary>
    public async Task<DBusMenu?> GetMenuAsync()
    {
        if (MenuPath is null)
            return null;
        var menu = new DBusMenu(_app, _connection, BusName, MenuPath);
        await menu.StartAsync();
        return menu;
    }

    private Task CallXY(string method, int x, int y) =>
        _connection.CallAsync(BusName, ObjectPath, Interface, method, (ref MessageWriter w) =>
        {
            w.WriteInt32(x);
            w.WriteInt32(y);
        }, "ii");

    internal void Stop()
    {
        foreach (var watch in _watches)
            watch.Dispose();
        _watches.Clear();
    }

    public override string ToString() => $"{Id} ({Service})";
}
