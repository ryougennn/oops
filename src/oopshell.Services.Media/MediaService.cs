using System.Collections.ObjectModel;
using oopshell.Services.DBus;
using Tmds.DBus.Protocol;

namespace oopshell.Services.Media;

public enum PlaybackStatus { Stopped, Playing, Paused }

/// <summary>One MPRIS media player (a bus name <c>org.mpris.MediaPlayer2.*</c>). Properties refresh when it signals a change.</summary>
public sealed class MediaPlayer
{
    const string Path = "/org/mpris/MediaPlayer2";
    const string PlayerInterface = "org.mpris.MediaPlayer2.Player";

    readonly DBusConnection connection;

    internal MediaPlayer(DBusConnection connection, string busName)
    {
        this.connection = connection;
        BusName = busName;
    }

    /// <summary>e.g. <c>org.mpris.MediaPlayer2.spotify</c>.</summary>
    public string BusName { get; }
    /// <summary>The player's name for people ("Spotify"), falling back to the end of the bus name.</summary>
    public string Identity { get; private set; } = "";
    /// <summary>The player's desktop file id without ".desktop", if it says.</summary>
    public string? DesktopEntry { get; private set; }
    public PlaybackStatus Status { get; private set; }
    public string Title { get; private set; } = "";
    /// <summary>The artists, joined with ", ".</summary>
    public string Artist { get; private set; } = "";
    public string Album { get; private set; } = "";
    /// <summary>Cover art, usually a file:// or https:// URL.</summary>
    public string? ArtUrl { get; private set; }
    public TimeSpan? Length { get; private set; }
    public bool CanPlay { get; private set; }
    public bool CanPause { get; private set; }
    public bool CanGoNext { get; private set; }
    public bool CanGoPrevious { get; private set; }

    /// <summary>The unique bus name that owns <see cref="BusName"/> (signals come from it).</summary>
    internal string Owner { get; set; } = "";

    /// <summary>When the player last changed (for picking the most recent one).</summary>
    public DateTime LastChanged { get; private set; }

    /// <summary>A property changed (on the UI thread).</summary>
    public event Action? Changed;

    public Task PlayPauseAsync() => Call("PlayPause");
    public Task NextAsync() => Call("Next");
    public Task PreviousAsync() => Call("Previous");
    public Task StopAsync() => Call("Stop");

    async Task Call(string method)
    {
        try
        {
            await connection.CallAsync(BusName, Path, PlayerInterface, method);
        }
        catch (DBusErrorReplyException)
        {
            // The player refused (e.g. nothing to skip to) or quit meanwhile.
        }
    }

    internal async Task<bool> RefreshAsync(OopsApp app)
    {
        Dictionary<string, VariantValue> player, root;
        try
        {
            player = await connection.GetAllPropertiesAsync(BusName, Path, PlayerInterface);
            root = await connection.GetAllPropertiesAsync(BusName, Path, "org.mpris.MediaPlayer2");
        }
        catch (DBusErrorReplyException)
        {
            return false;
        }
        await app.InvokeAsync(() =>
        {
            Apply(player, root);
            return Task.CompletedTask;
        });
        return true;
    }

    void Apply(Dictionary<string, VariantValue> player, Dictionary<string, VariantValue> root)
    {
        Identity = Str(root, "Identity") is { Length: > 0 } identity ? identity : BusName[(BusName.LastIndexOf('.') + 1)..];
        DesktopEntry = Str(root, "DesktopEntry");
        Status = Str(player, "PlaybackStatus") switch { "Playing" => PlaybackStatus.Playing, "Paused" => PlaybackStatus.Paused, _ => PlaybackStatus.Stopped };
        CanPlay = Bool(player, "CanPlay");
        CanPause = Bool(player, "CanPause");
        CanGoNext = Bool(player, "CanGoNext");
        CanGoPrevious = Bool(player, "CanGoPrevious");
        Title = Artist = Album = "";
        ArtUrl = null;
        Length = null;
        if (player.TryGetValue("Metadata", out var metadata) && Unwrap(metadata) is { Type: VariantValueType.Dictionary } dict)
        {
            for (int i = 0; i < dict.Count; i++)
            {
                var entry = dict.GetDictionaryEntry(i);
                var value = Unwrap(entry.Value);
                switch (entry.Key.GetString())
                {
                    case "xesam:title" when value.Type == VariantValueType.String: Title = value.GetString(); break;
                    case "xesam:album" when value.Type == VariantValueType.String: Album = value.GetString(); break;
                    case "mpris:artUrl" when value.Type == VariantValueType.String: ArtUrl = value.GetString(); break;
                    case "xesam:artist" when value.Type == VariantValueType.Array:
                        Artist = string.Join(", ", Enumerable.Range(0, value.Count).Select(j => Unwrap(value.GetItem(j))).Where(v => v.Type == VariantValueType.String).Select(v => v.GetString()));
                        break;
                    case "xesam:artist" when value.Type == VariantValueType.String: Artist = value.GetString(); break; // some players send one string
                    case "mpris:length" when value.Type is VariantValueType.Int64: Length = TimeSpan.FromMicroseconds(value.GetInt64()); break;
                    case "mpris:length" when value.Type is VariantValueType.UInt64: Length = TimeSpan.FromMicroseconds(value.GetUInt64()); break;
                }
            }
        }
        LastChanged = DateTime.UtcNow;
        Changed?.Invoke();

        static VariantValue Unwrap(VariantValue v) => v.Type == VariantValueType.Variant ? v.GetVariantValue() : v;
        static string? Str(Dictionary<string, VariantValue> p, string key) => p.TryGetValue(key, out var v) && Unwrap(v) is { Type: VariantValueType.String } s ? s.GetString() : null;
        static bool Bool(Dictionary<string, VariantValue> p, string key) => p.TryGetValue(key, out var v) && Unwrap(v) is { Type: VariantValueType.Bool } b && b.GetBool();
    }

    public override string ToString() => $"{Identity}: {Status} {Artist} – {Title}";
}

/// <summary>
/// Media players on the session bus (MPRIS): they appear and disappear as they start and quit.
/// <code>
/// var media = await MediaService.StartAsync(app, sessionBus);
/// media.Active?.PlayPauseAsync();
/// </code>
/// All state changes and events happen on the UI thread.
/// </summary>
public sealed class MediaService : IDisposable
{
    public const string BusNamePrefix = "org.mpris.MediaPlayer2.";

    readonly OopsApp app;
    readonly DBusConnection connection;
    readonly ObservableCollection<MediaPlayer> players = new();
    readonly List<IDisposable> watches = new();

    MediaService(OopsApp app, DBusConnection connection)
    {
        this.app = app;
        this.connection = connection;
        Players = new ReadOnlyObservableCollection<MediaPlayer>(players);
    }

    public static async Task<MediaService> StartAsync(OopsApp app, DBusConnection connection)
    {
        var service = new MediaService(app, connection);
        await service.InitializeAsync();
        return service;
    }

    public ReadOnlyObservableCollection<MediaPlayer> Players { get; }

    /// <summary>The player to show: one that is playing, else the one that changed last.</summary>
    public MediaPlayer? Active =>
        players.Where(p => p.Status == PlaybackStatus.Playing).MaxBy(p => p.LastChanged) ?? players.MaxBy(p => p.LastChanged);

    /// <summary>A player appeared, quit, or changed (on the UI thread).</summary>
    public event Action? Changed;

    async Task InitializeAsync()
    {
        watches.Add(await connection.WatchSignalAsync(app, "org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "NameOwnerChanged",
            (message, _) =>
            {
                var reader = message.GetBodyReader();
                return (Name: reader.ReadString(), Old: reader.ReadString(), New: reader.ReadString());
            },
            change =>
            {
                if (!change.Name.StartsWith(BusNamePrefix, StringComparison.Ordinal))
                    return;
                if (change.New.Length == 0)
                    Remove(change.Name);
                else if (players.FirstOrDefault(p => p.BusName == change.Name) is { } known)
                    known.Owner = change.New;
                else
                    _ = AddAsync(change.Name);
            }));
        watches.Add(await connection.WatchSignalAsync(app, null, "/org/mpris/MediaPlayer2", "org.freedesktop.DBus.Properties", "PropertiesChanged",
            (message, _) => message.SenderAsString ?? "",
            sender => _ = RefreshFromAsync(sender)));
        var names = await connection.CallAsync("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "ListNames",
            (message, _) => message.GetBodyReader().ReadArrayOfString());
        foreach (var name in names.Where(n => n.StartsWith(BusNamePrefix, StringComparison.Ordinal)))
            await AddAsync(name);
    }

    async Task AddAsync(string busName)
    {
        if (players.Any(p => p.BusName == busName))
            return;
        var player = new MediaPlayer(connection, busName);
        player.Changed += () => Changed?.Invoke();
        try
        {
            player.Owner = await connection.CallAsync("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "GetNameOwner",
                (message, _) => message.GetBodyReader().ReadString(), (ref MessageWriter w) => w.WriteString(busName), "s");
        }
        catch (DBusErrorReplyException)
        {
            return; // quit meanwhile
        }
        if (!await player.RefreshAsync(app))
            return;
        await app.InvokeAsync(() =>
        {
            if (players.All(p => p.BusName != busName))
            {
                players.Add(player);
                Changed?.Invoke();
            }
            return Task.CompletedTask;
        });
    }

    void Remove(string busName)
    {
        if (players.FirstOrDefault(p => p.BusName == busName) is { } player)
        {
            players.Remove(player);
            Changed?.Invoke();
        }
    }

    /// <summary>PropertiesChanged comes from the player's unique name (":1.42"), not its well-known one.</summary>
    async Task RefreshFromAsync(string sender)
    {
        foreach (var player in players.Where(p => p.Owner == sender).ToList())
            await player.RefreshAsync(app);
    }

    public void Dispose()
    {
        foreach (var watch in watches)
            watch.Dispose();
        watches.Clear();
    }
}
