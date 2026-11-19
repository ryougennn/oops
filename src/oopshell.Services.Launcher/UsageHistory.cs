using System.Text.Json;
using System.Text.Json.Serialization;

namespace oopshell.Services.Launcher;

/// <summary>
/// How often and how recently each application was launched ("frecency"), so search can prefer what the
/// user actually uses. Saved as JSON (default <c>$XDG_STATE_HOME/oopshell/launcher-usage.json</c>).
/// </summary>
public sealed class UsageHistory
{
    public sealed class Record
    {
        public int Count { get; set; }
        public long LastUsed { get; set; } // unix seconds
    }

    readonly string? path;
    readonly Dictionary<string, Record> records;

    UsageHistory(string? path, Dictionary<string, Record> records)
    {
        this.path = path;
        this.records = records;
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } state ? state : Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "/", ".local/state"),
        "oopshell", "launcher-usage.json");

    /// <summary>Loads the history from <paramref name="path"/> (default <see cref="DefaultPath"/>); a missing or corrupt file starts empty.</summary>
    public static UsageHistory Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize(File.ReadAllText(path), UsageJson.Default.DictionaryStringRecord) is { } loaded)
                return new UsageHistory(path, loaded.Where(e => e.Value is not null).ToDictionary()); // {"x": null} is valid JSON
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new UsageHistory(path, new());
    }

    /// <summary>A history that is never saved (tests, private sessions).</summary>
    public static UsageHistory InMemory() => new(null, new());

    public void RecordLaunch(string desktopId, DateTimeOffset? now = null)
    {
        if (!records.TryGetValue(desktopId, out var record))
            records[desktopId] = record = new Record();
        record.Count++;
        record.LastUsed = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        Save();
    }

    /// <summary>0..20: grows with the launch count, fades over weeks.</summary>
    public double Boost(string desktopId, DateTimeOffset? now = null)
    {
        if (!records.TryGetValue(desktopId, out var record))
            return 0;
        double ageDays = ((now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() - record.LastUsed) / 86400.0;
        double recency = ageDays < 1 ? 1 : ageDays < 7 ? 0.7 : ageDays < 30 ? 0.4 : 0.2;
        return Math.Min(20, 6 * Math.Log(1 + record.Count) * recency);
    }

    void Save()
    {
        if (path is null)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(records, UsageJson.Default.DictionaryStringRecord));
            File.Move(temp, path, overwrite: true); // atomic: never a half-written file
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

[JsonSerializable(typeof(Dictionary<string, UsageHistory.Record>))]
internal sealed partial class UsageJson : JsonSerializerContext;
