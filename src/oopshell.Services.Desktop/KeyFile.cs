using System.Text;

namespace oopshell.Services.Desktop;

/// <summary>Parser for the freedesktop "key file" format (.desktop files, index.theme).</summary>
public sealed class KeyFile
{
    private readonly Dictionary<string, Dictionary<string, string>> _groups = new();

    public static KeyFile Parse(string text)
    {
        var file = new KeyFile();
        Dictionary<string, string>? group = null;
        // Some files start with a UTF-8 byte order mark, which would hide the first group.
        if (text.Length > 0 && text[0] == '\uFEFF')
            text = text[1..];
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').TrimStart(' ', '\t');
            if (line.Length == 0 || line[0] == '#')
                continue;
            if (line[0] == '[' && line.EndsWith(']'))
            {
                string name = line[1..^1];
                // The spec forbids duplicate groups; keep the first.
                group = file._groups.TryAdd(name, new()) ? file._groups[name] : null;
                continue;
            }
            int eq = line.IndexOf('=');
            if (group is null || eq <= 0)
                continue;
            group.TryAdd(line[..eq].Trim(), line[(eq + 1)..].Trim());
        }
        return file;
    }

    public static KeyFile Load(string path) => Parse(File.ReadAllText(path));

    /// <summary><see cref="Load"/>, or null if the file is missing or unreadable.</summary>
    public static KeyFile? TryLoad(string path)
    {
        try
        {
            return File.Exists(path) ? Load(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public IEnumerable<string> Groups => _groups.Keys;

    public string? Get(string group, string key) =>
        _groups.TryGetValue(group, out var g) && g.TryGetValue(key, out var value) ? Unescape(value) : null;

    /// <summary>A localized value: tries <c>key[lang_COUNTRY@MODIFIER]</c>, <c>key[lang_COUNTRY]</c>, <c>key[lang@MODIFIER]</c>, <c>key[lang]</c>, then <c>key</c>.</summary>
    public string? GetLocalized(string group, string key, string? locale)
    {
        foreach (var candidate in LocaleCandidates(locale))
        {
            if (Get(group, $"{key}[{candidate}]") is { } value)
                return value;
        }
        return Get(group, key);
    }

    public bool GetBool(string group, string key) => Get(group, key) == "true";

    /// <summary>A list separated by <paramref name="separator"/> (';' in desktop files, ',' in icon themes); the separator can be escaped.</summary>
    public IReadOnlyList<string> GetList(string group, string key, char separator = ';')
    {
        if (!_groups.TryGetValue(group, out var g) || !g.TryGetValue(key, out var raw))
            return [];
        var items = new List<string>();
        var current = new StringBuilder();
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '\\' && i + 1 < raw.Length && raw[i + 1] == '\\')
            {
                current.Append("\\\\"); // an escaped backslash, left for Unescape; it cannot escape the next separator
                i++;
            }
            else if (raw[i] == '\\' && i + 1 < raw.Length && raw[i + 1] == separator)
            {
                current.Append(separator);
                i++;
            }
            else if (raw[i] == separator)
            {
                items.Add(Unescape(current.ToString().Trim()));
                current.Clear();
            }
            else
            {
                current.Append(raw[i]);
            }
        }
        string last = current.ToString().Trim();
        if (last.Length > 0)
            items.Add(Unescape(last));
        return items;
    }

    private static string Unescape(string value)
    {
        if (!value.Contains('\\'))
            return value;
        var sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 == value.Length)
            {
                sb.Append(value[i]);
                continue;
            }
            char next = value[++i];
            sb.Append(next switch { 's' => ' ', 'n' => '\n', 't' => '\t', 'r' => '\r', _ => next });
        }
        return sb.ToString();
    }

    /// <summary>"de_DE.UTF-8@euro" → de_DE@euro, de_DE, de@euro, de.</summary>
    internal static IEnumerable<string> LocaleCandidates(string? locale)
    {
        if (string.IsNullOrEmpty(locale) || locale is "C" or "POSIX")
            yield break;
        string modifier = "";
        int at = locale.IndexOf('@');
        if (at >= 0)
        {
            modifier = locale[at..];
            locale = locale[..at];
        }
        int dot = locale.IndexOf('.');
        if (dot >= 0)
            locale = locale[..dot];
        int underscore = locale.IndexOf('_');
        string lang = underscore >= 0 ? locale[..underscore] : locale;
        if (underscore >= 0)
        {
            if (modifier.Length > 0)
                yield return locale + modifier;
            yield return locale;
        }
        if (modifier.Length > 0)
            yield return lang + modifier;
        yield return lang;
    }
}
