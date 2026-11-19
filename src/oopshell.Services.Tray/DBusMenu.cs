using oopshell.Services.DBus;
using Tmds.DBus.Protocol;

namespace oopshell.Services.Tray;

/// <summary>One entry of a <see cref="DBusMenu"/>.</summary>
public sealed class DBusMenuItem
{
    public int Id { get; init; }
    /// <summary>Label with the '_' mnemonic markers removed (a literal underscore is "__").</summary>
    public string Label { get; init; } = "";
    /// <summary>The character after the first single '_', if any.</summary>
    public char? Mnemonic { get; init; }
    public bool Enabled { get; init; } = true;
    public bool Visible { get; init; } = true;
    public bool IsSeparator { get; init; }
    /// <summary>"checkmark", "radio", or empty.</summary>
    public string ToggleType { get; init; } = "";
    /// <summary>0 off, 1 on, -1 indeterminate.</summary>
    public int ToggleState { get; init; } = -1;
    public string? IconName { get; init; }
    /// <summary>PNG bytes of the item's icon, if it ships one.</summary>
    public byte[]? IconPng { get; init; }
    public IReadOnlyList<DBusMenuItem> Children { get; init; } = [];
    public bool HasSubmenu => Children.Count > 0;
}

/// <summary>
/// The menu model of a <c>com.canonical.dbusmenu</c> object (tray item menus). oopshell only exposes the tree;
/// draw it however you like and call <see cref="ClickAsync"/>.
/// </summary>
public sealed class DBusMenu : IDisposable
{
    private const string Interface = "com.canonical.dbusmenu";
    private readonly OopsApp _app;
    private readonly DBusConnection _connection;
    private readonly string _busName;
    private readonly string _path;
    private readonly List<IDisposable> _watches = new();

    internal DBusMenu(OopsApp app, DBusConnection connection, string busName, string path)
    {
        _app = app;
        _connection = connection;
        _busName = busName;
        _path = path;
    }

    /// <summary>The root item; its <see cref="DBusMenuItem.Children"/> are the top-level entries.</summary>
    public DBusMenuItem Root { get; private set; } = new();

    /// <summary>The layout or item properties changed and <see cref="Root"/> was reloaded.</summary>
    public event Action? Changed;

    internal async Task StartAsync()
    {
        await RefreshAsync();
        _watches.Add(await _connection.WatchSignalAsync(_app, _busName, _path, Interface, "LayoutUpdated", (_, _) => true, OnChangeSignal));
        _watches.Add(await _connection.WatchSignalAsync(_app, _busName, _path, Interface, "ItemsPropertiesUpdated", (_, _) => true, OnChangeSignal));
    }

    // Fire and forget: a failed refresh keeps the previous state.
    private void OnChangeSignal(bool signal) => _ = RefreshAsync();

    /// <summary>Reloads the whole layout.</summary>
    public async Task RefreshAsync()
    {
        var root = await _connection.CallAsync(_busName, _path, Interface, "GetLayout",
            (message, _) =>
            {
                var reader = message.GetBodyReader();
                reader.ReadUInt32(); // revision
                return ReadItem(ref reader);
            },
            (ref MessageWriter w) =>
            {
                w.WriteInt32(0);   // from the root
                w.WriteInt32(-1);  // all levels
                w.WriteArray(Array.Empty<string>()); // all properties
            }, "iias");
        await _app.InvokeAsync(() =>
        {
            Root = root;
            Changed?.Invoke();
            return Task.CompletedTask;
        });
    }

    /// <summary>Tells the app the user is about to open <paramref name="item"/>'s submenu (lets it fill it lazily).</summary>
    public async Task AboutToShowAsync(DBusMenuItem item)
    {
        bool needsUpdate = await _connection.CallAsync(_busName, _path, Interface, "AboutToShow",
            (message, _) => message.GetBodyReader().ReadBool(), (ref MessageWriter w) => w.WriteInt32(item.Id), "i");
        if (needsUpdate)
            await RefreshAsync();
    }

    /// <summary>Activates <paramref name="item"/>.</summary>
    public Task ClickAsync(DBusMenuItem item) =>
        _connection.CallAsync(_busName, _path, Interface, "Event", (ref MessageWriter w) =>
        {
            w.WriteInt32(item.Id);
            w.WriteString("clicked");
            w.WriteVariantInt32(0);
            w.WriteUInt32((uint)Environment.TickCount);
        }, "isvu");

    /// <summary>Reads an <c>(ia{sv}av)</c> layout node.</summary>
    internal static DBusMenuItem ReadItem(ref Reader reader)
    {
        reader.AlignStruct();
        int id = reader.ReadInt32();
        var props = reader.ReadDictionaryOfStringToVariantValue();
        var children = new List<DBusMenuItem>();
        var end = reader.ReadArrayStart(DBusType.Variant);
        while (reader.HasNext(end))
        {
            reader.ReadSignature("(ia{sv}av)");
            children.Add(ReadItem(ref reader));
        }
        return FromProperties(id, props, children);
    }

    internal static DBusMenuItem FromProperties(int id, Dictionary<string, VariantValue> p, List<DBusMenuItem> children)
    {
        string? Str(string key) => p.TryGetValue(key, out var v) && v.Type == VariantValueType.String ? v.GetString() : null;
        bool Bool(string key, bool fallback) => p.TryGetValue(key, out var v) && v.Type == VariantValueType.Bool ? v.GetBool() : fallback;
        var (label, mnemonic) = ParseLabel(Str("label") ?? "");
        return new DBusMenuItem
        {
            Id = id,
            Label = label,
            Mnemonic = mnemonic,
            Enabled = Bool("enabled", true),
            Visible = Bool("visible", true),
            IsSeparator = Str("type") == "separator",
            ToggleType = Str("toggle-type") ?? "",
            ToggleState = p.TryGetValue("toggle-state", out var state) && state.Type == VariantValueType.Int32 ? state.GetInt32() : -1,
            IconName = Str("icon-name") is { Length: > 0 } icon ? icon : null,
            IconPng = p.TryGetValue("icon-data", out var data) && data.Type == VariantValueType.Array ? data.GetArray<byte>() : null,
            Children = children.Where(c => c.Visible).ToList(),
        };
    }

    /// <summary>"_Open" → ("Open", 'O'); "Save __as" → ("Save _as", null).</summary>
    internal static (string Label, char? Mnemonic) ParseLabel(string raw)
    {
        var sb = new System.Text.StringBuilder(raw.Length);
        char? mnemonic = null;
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '_' && i + 1 < raw.Length)
            {
                if (raw[i + 1] == '_')
                {
                    sb.Append('_');
                    i++;
                    continue;
                }
                mnemonic ??= raw[i + 1];
                continue;
            }
            sb.Append(raw[i]);
        }
        return (sb.ToString(), mnemonic);
    }

    public void Dispose()
    {
        foreach (var watch in _watches)
            watch.Dispose();
        _watches.Clear();
    }
}
