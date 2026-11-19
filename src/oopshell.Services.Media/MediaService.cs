using System.Collections.ObjectModel;
using oopshell.Services.DBus;
using Tmds.DBus.Protocol;

namespace oopshell.Services.Media;

public enum PlaybackStatus { Stopped, Playing, Paused }

/// <summary>One MPRIS media player (a bus name <c>org.mpris.MediaPlayer2.*</c>). Properties refresh when it signals a change.</summary>
public sealed class MediaPlayer
{
    private const string Path = "/org/mpris/MediaPlayer2";
    private const string PlayerInterface = "org.mpris.MediaPlayer2.Player";

    private readonly DBusConnection _connection;

    internal MediaPlayer(DBusConnection connection, string busName)
    {
        _connection = connection;
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

    private async Task Call(string method)
    {
        try
        {
            await _connection.CallAsync(BusName, Path, PlayerInterface, method);
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
            player = await _connection.GetAllPropertiesAsync(BusName, Path, PlayerInterface);
            root = await _connection.GetAllPropertiesAsync(BusName, Path, "org.mpris.MediaPlayer2");
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

    private void Apply(Dictionary<string, VariantValue> player, Dictionary<string, VariantValue> root)
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
                // Any player can put anything here: check every type, clip strings, bound the length.
                switch (entry.Key.AsString())
                {
                    case "xesam:title": Title = value.AsString()?.Clip(1024) ?? Title; break;
                    case "xesam:album": Album = value.AsString()?.Clip(1024) ?? Album; break;
                    case "mpris:artUrl": ArtUrl = value.AsString()?.Clip(4096); break;
                    case "xesam:artist":
                        // A list of artists, or (some players) one string.
                        Artist = (value.AsStrings() is { } artists ? string.Join(", ", artists.Take(16)) : value.AsString() ?? Artist).Clip(1024);
                        break;
                    case "mpris:length":
                        Length = value.AsInt64() is long us and > 0 and < TimeSpan.TicksPerDay * 365 / 10 ? TimeSpan.FromMicroseconds(us) : null;
                        break;
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

    private readonly OopsApp _app;
    private readonly DBusConnection _connection;
    private readonly ObservableCollection<MediaPlayer> _players = new();
    private readonly List<IDisposable> _watches = new();

    private MediaService(OopsApp app, DBusConnection connection)
    {
        _app = app;
        _connection = connection;
        Players = new ReadOnlyObservableCollection<MediaPlayer>(_players);
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
        _players.Where(p => p.Status == PlaybackStatus.Playing).MaxBy(p => p.LastChanged) ?? _players.MaxBy(p => p.LastChanged);

    /// <summary>A player appeared, quit, or changed (on the UI thread).</summary>
    public event Action? Changed;

    private async Task InitializeAsync()
    {
        _watches.Add(await _connection.WatchSignalAsync(_app, "org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "NameOwnerChanged",
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
                else if (_players.FirstOrDefault(p => p.BusName == change.Name) is { } known)
                    known.Owner = change.New;
                else
                    _ = AddAsync(change.Name);
            }));
        _watches.Add(await _connection.WatchSignalAsync(_app, null, "/org/mpris/MediaPlayer2", "org.freedesktop.DBus.Properties", "PropertiesChanged",
            (message, _) => message.SenderAsString ?? "",
            sender => _ = RefreshFromAsync(sender)));
        var names = await _connection.CallAsync("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "ListNames",
            (message, _) => message.GetBodyReader().ReadArrayOfString());
        await Task.WhenAll(names.Where(n => n.StartsWith(BusNamePrefix, StringComparison.Ordinal)).Select(AddAsync));
    }

    private async Task AddAsync(string busName)
    {
        if (_players.Any(p => p.BusName == busName))
            return;
        var player = new MediaPlayer(_connection, busName);
        player.Changed += () => Changed?.Invoke();
        try
        {
            player.Owner = await _connection.CallAsync("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "GetNameOwner",
                (message, _) => message.GetBodyReader().ReadString(), (ref MessageWriter w) => w.WriteString(busName), "s");
        }
        catch (DBusErrorReplyException)
        {
            return; // quit meanwhile
        }
        try
        {
            if (!await player.RefreshAsync(_app).WaitAsync(TimeSpan.FromSeconds(3)))
                return;
        }
        catch (Exception)
        {
            return; // a player that hangs or answers nonsense is left out; the others still show
        }
        await _app.InvokeAsync(() =>
        {
            if (_players.All(p => p.BusName != busName))
            {
                _players.Add(player);
                Changed?.Invoke();
            }
            return Task.CompletedTask;
        });
    }

    private void Remove(string busName)
    {
        if (_players.FirstOrDefault(p => p.BusName == busName) is { } player)
        {
            _players.Remove(player);
            Changed?.Invoke();
        }
    }

    /// <summary>PropertiesChanged comes from the player's unique name (":1.42"), not its well-known one.</summary>
    private async Task RefreshFromAsync(string sender)
    {
        foreach (var player in _players.Where(p => p.Owner == sender).ToList())
            await player.RefreshAsync(_app);
    }

    public void Dispose()
    {
        foreach (var watch in _watches)
            watch.Dispose();
        _watches.Clear();
    }
}
