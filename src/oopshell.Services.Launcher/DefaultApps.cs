using oopshell.Services.Desktop;

namespace oopshell.Services.Launcher;

/// <summary>
/// File types from the shared-mime-info database (<c>mime/globs2</c>, <c>subclasses</c>, <c>aliases</c> under
/// the XDG data dirs): by file name, plus directories (<c>inode/directory</c>) and URL schemes
/// (<c>x-scheme-handler/https</c>). Content sniffing is not done.
/// </summary>
public sealed class MimeDatabase
{
    readonly Dictionary<string, (int Weight, string Mime)> extensions = new();   // "*.ext" patterns, lower case
    readonly List<(int Weight, string Mime, string Pattern, bool CaseSensitive)> patterns = new();
    readonly Dictionary<string, List<string>> parents = new();
    readonly Dictionary<string, string> aliases = new();

    public static MimeDatabase Load(IEnumerable<string>? dataDirs = null)
    {
        var db = new MimeDatabase();
        foreach (var dir in dataDirs ?? DesktopEntries.DataDirs())
        {
            string mime = Path.Combine(dir, "mime");
            db.ReadGlobs(Path.Combine(mime, "globs2"));
            foreach (var (child, parent) in Pairs(Path.Combine(mime, "subclasses")))
            {
                if (!db.parents.TryGetValue(child, out var list))
                    db.parents[child] = list = new();
                if (!list.Contains(parent))
                    list.Add(parent);
            }
            foreach (var (alias, canonical) in Pairs(Path.Combine(mime, "aliases")))
                db.aliases.TryAdd(alias, canonical);
        }
        return db;
    }

    /// <summary>A file's lines, or none if it is missing or unreadable.</summary>
    static string[] Lines(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllLines(path) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    static IEnumerable<(string, string)> Pairs(string path)
    {
        foreach (var line in Lines(path))
        {
            var parts = line.Split(' ', 2);
            if (parts.Length == 2 && !line.StartsWith('#'))
                yield return (parts[0], parts[1].Trim());
        }
    }

    void ReadGlobs(string path)
    {
        foreach (var line in Lines(path))
        {
            if (line.StartsWith('#'))
                continue;
            var parts = line.Split(':');
            if (parts.Length < 3 || !int.TryParse(parts[0], out int weight))
                continue;
            string mime = parts[1], pattern = parts[2];
            bool caseSensitive = parts.Length > 3 && parts[3].Split(',').Contains("cs");
            if (!caseSensitive && pattern.StartsWith("*.", StringComparison.Ordinal) && pattern.IndexOfAny(['*', '?', '['], 1) < 0)
            {
                string ext = pattern[1..].ToLowerInvariant();
                // Data dirs are read in priority order; keep the first, unless a later one weighs more.
                if (!extensions.TryGetValue(ext, out var existing) || weight > existing.Weight)
                    extensions[ext] = (weight, mime);
            }
            else
                patterns.Add((weight, mime, pattern, caseSensitive));
        }
    }

    /// <summary>The MIME type of a file name ("report.PDF" → application/pdf); application/octet-stream if unknown.</summary>
    public string FromFileName(string fileName)
    {
        fileName = Path.GetFileName(fileName);
        (int Weight, string Mime, int Length) best = (-1, "application/octet-stream", 0);
        foreach (var (weight, mime, pattern, caseSensitive) in patterns)
        {
            if (Glob(pattern, fileName, caseSensitive) && (weight > best.Weight || weight == best.Weight && pattern.Length > best.Length))
                best = (weight, mime, pattern.Length);
        }
        // The longest matching extension wins ("archive.tar.gz" is a .tar.gz, not a .gz).
        string lower = fileName.ToLowerInvariant();
        for (int dot = lower.IndexOf('.'); dot >= 0; dot = lower.IndexOf('.', dot + 1))
        {
            if (extensions.TryGetValue(lower[dot..], out var hit))
            {
                if (hit.Weight > best.Weight || hit.Weight == best.Weight && lower.Length - dot + 1 > best.Length)
                    best = (hit.Weight, hit.Mime, lower.Length - dot + 1);
                break;
            }
        }
        return best.Mime;
    }

    /// <summary>The type of a path or URL: directories, URL schemes, then the file name.</summary>
    public string Guess(string target)
    {
        if (TryScheme(target) is { } scheme && scheme != "file")
            return $"x-scheme-handler/{scheme}";
        string path = target.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ? FilePath(target) ?? "" : target;
        if (Directory.Exists(path))
            return "inode/directory";
        return FromFileName(path);
    }

    /// <summary>The local path of a file:// URL, or null if it is not a valid one.</summary>
    internal static string? FilePath(string url)
    {
        try
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : null;
        }
        catch (Exception e) when (e is IndexOutOfRangeException or UriFormatException)
        {
            return null; // System.Uri throws on some malformed input instead of returning false
        }
    }

    /// <summary>"https://x" → https; "/a/b" and "C:" style are not URLs.</summary>
    internal static string? TryScheme(string target)
    {
        int colon = target.IndexOf(':');
        if (colon < 2 || !char.IsAsciiLetter(target[0]))
            return null;
        for (int i = 1; i < colon; i++)
        {
            if (!(char.IsAsciiLetterOrDigit(target[i]) || target[i] is '+' or '-' or '.'))
                return null;
        }
        return target[..colon].ToLowerInvariant();
    }

    /// <summary>The type, its canonical name, its ancestors (subclasses), then the implicit text/plain and application/octet-stream.</summary>
    public IEnumerable<string> WithParents(string mime)
    {
        var seen = new HashSet<string>();
        var queue = new Queue<string>();
        queue.Enqueue(aliases.GetValueOrDefault(mime, mime));
        while (queue.TryDequeue(out var current))
        {
            if (!seen.Add(current))
                continue;
            yield return current;
            foreach (var parent in parents.GetValueOrDefault(current) ?? [])
                queue.Enqueue(parent);
        }
        if (mime.StartsWith("text/", StringComparison.Ordinal) && seen.Add("text/plain"))
            yield return "text/plain";
        if (!mime.StartsWith("inode/", StringComparison.Ordinal) && !mime.StartsWith("x-scheme-handler/", StringComparison.Ordinal) && seen.Add("application/octet-stream"))
            yield return "application/octet-stream";
    }

    /// <summary>Shell-style * and ? (character classes are rare in globs2 and treated literally).</summary>
    static bool Glob(string pattern, string name, bool caseSensitive)
    {
        if (!caseSensitive)
        {
            pattern = pattern.ToLowerInvariant();
            name = name.ToLowerInvariant();
        }
        int p = 0, n = 0, star = -1, mark = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == name[n]))
            {
                p++;
                n++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = n;
            }
            else if (star >= 0)
            {
                p = star + 1;
                n = ++mark;
            }
            else
                return false;
        }
        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }
}

/// <summary>
/// The user's default applications, from <c>mimeapps.list</c> files as the freedesktop spec orders them
/// (desktop-specific ones first; config home, config dirs, then data dirs), falling back along the type's
/// parents and to any application that declares the type.
/// </summary>
public static class DefaultApps
{
    /// <summary>The mimeapps.list files, highest priority first.</summary>
    public static IReadOnlyList<string> ListFiles(IEnumerable<string>? currentDesktops = null, IEnumerable<string>? dataDirs = null)
    {
        var desktops = (currentDesktops ?? (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
            .Select(d => d.ToLowerInvariant()).ToList();
        string home = Environment.GetEnvironmentVariable("HOME") ?? "/";
        string configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c : Path.Combine(home, ".config");
        var configDirs = (Environment.GetEnvironmentVariable("XDG_CONFIG_DIRS") is { Length: > 0 } d ? d : "/etc/xdg").Split(':', StringSplitOptions.RemoveEmptyEntries);
        var dirs = new[] { configHome }.Concat(configDirs)
            .Concat((dataDirs ?? DesktopEntries.DataDirs()).Select(dir => Path.Combine(dir, "applications")));
        var files = new List<string>();
        foreach (var dir in dirs.Distinct())
        {
            files.AddRange(desktops.Select(desktop => Path.Combine(dir, $"{desktop}-mimeapps.list")));
            files.Add(Path.Combine(dir, "mimeapps.list"));
        }
        return files;
    }

    /// <summary>The application that opens <paramref name="mime"/>, or null. <paramref name="apps"/> should include NoDisplay entries (handlers often are).</summary>
    public static DesktopEntry? For(string mime, IReadOnlyList<DesktopEntry> apps, MimeDatabase mimeDatabase, IReadOnlyList<string>? listFiles = null)
    {
        var lists = (listFiles ?? ListFiles()).Select(KeyFile.TryLoad).OfType<KeyFile>().ToList();
        var byId = apps.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var type in mimeDatabase.WithParents(mime))
        {
            var removed = lists.SelectMany(l => l.GetList("Removed Associations", type)).ToHashSet();
            DesktopEntry? Pick(IEnumerable<string> ids) =>
                ids.Where(id => !removed.Contains(id)).Select(id => byId.GetValueOrDefault(id)).FirstOrDefault(e => e is { Exec: not null });
            foreach (var list in lists)
            {
                if (Pick(list.GetList("Default Applications", type)) is { } chosen)
                    return chosen;
            }
            foreach (var list in lists)
            {
                if (Pick(list.GetList("Added Associations", type)) is { } added)
                    return added;
            }
            if (apps.Where(a => a.MimeTypes.Contains(type) && a.Exec is not null && !a.Hidden && !removed.Contains(a.Id))
                    .OrderBy(a => a.NoDisplay).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault() is { } declared)
                return declared;
        }
        return null;
    }
}
