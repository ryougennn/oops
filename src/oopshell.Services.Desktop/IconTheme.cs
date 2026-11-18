namespace oopshell.Services.Desktop;

/// <summary>
/// Icon lookup following the freedesktop Icon Theme Specification: the requested theme, its parents,
/// then hicolor, then <c>/usr/share/pixmaps</c>. PNG is preferred over SVG; XPM is ignored.
/// </summary>
public sealed class IconTheme
{
    static readonly string[] Extensions = [".png", ".svg"];

    readonly List<ThemeData> chain = new();
    readonly IReadOnlyList<string> baseDirs;
    readonly Dictionary<(string, int, int), string?> cache = new();

    sealed record ThemeData(string Name, List<string> Roots, List<ThemeDirectory> Directories);

    sealed record ThemeDirectory(string Path, int Size, int Scale, string Type, int MinSize, int MaxSize, int Threshold)
    {
        public bool Matches(int size, int scale) => Scale == scale && Type switch
        {
            "Fixed" => Size == size,
            "Scalable" => MinSize <= size && size <= MaxSize,
            _ => Size - Threshold <= size && size <= Size + Threshold,
        };

        public int Distance(int size, int scale) => Type switch
        {
            "Fixed" => Math.Abs(Size * Scale - size * scale),
            "Scalable" => size * scale < MinSize * Scale ? MinSize * Scale - size * scale
                : size * scale > MaxSize * Scale ? size * scale - MaxSize * Scale : 0,
            _ => size * scale < (Size - Threshold) * Scale ? (Size - Threshold) * Scale - size * scale
                : size * scale > (Size + Threshold) * Scale ? size * scale - (Size + Threshold) * Scale : 0,
        };
    }

    /// <param name="themeName">e.g. "Adwaita"; null or unknown falls back to hicolor.</param>
    /// <param name="baseDirs">Icon base directories; defaults to <c>~/.icons</c>, <c>$XDG_DATA_DIRS/icons</c>, <c>/usr/share/pixmaps</c>.</param>
    public IconTheme(string? themeName = null, IReadOnlyList<string>? baseDirs = null)
    {
        this.baseDirs = baseDirs ?? DefaultBaseDirs();
        var visited = new HashSet<string>();
        var pending = new Queue<string>();
        pending.Enqueue(themeName ?? "hicolor");
        while (pending.TryDequeue(out var name))
        {
            if (!visited.Add(name) || LoadTheme(name) is not { } theme)
                continue;
            chain.Add(theme);
            foreach (var parent in ReadInherits(name))
                pending.Enqueue(parent);
        }
        if (!visited.Contains("hicolor") && LoadTheme("hicolor") is { } hicolor)
            chain.Add(hicolor);
    }

    public IReadOnlyList<string> ThemeNames => chain.Select(t => t.Name).ToList();

    /// <summary>
    /// The file for <paramref name="icon"/> at <paramref name="size"/> logical pixels and integer
    /// <paramref name="scale"/>, or null. Absolute paths are returned as-is when they exist.
    /// </summary>
    public string? Find(string icon, int size = 48, int scale = 1)
    {
        if (System.IO.Path.IsPathRooted(icon))
            return File.Exists(icon) ? icon : null;
        if (cache.TryGetValue((icon, size, scale), out var cached))
            return cached;
        string? result = null;
        foreach (var theme in chain)
        {
            if ((result = FindInTheme(theme, icon, size, scale)) is not null)
                break;
        }
        result ??= FindUnthemed(icon);
        cache[(icon, size, scale)] = result;
        return result;
    }

    static string? FindInTheme(ThemeData theme, string icon, int size, int scale)
    {
        foreach (var dir in theme.Directories.Where(d => d.Matches(size, scale)))
        {
            if (Existing(dir.Path, icon) is { } exact)
                return exact;
        }
        string? best = null;
        int bestDistance = int.MaxValue;
        foreach (var dir in theme.Directories)
        {
            int distance = dir.Distance(size, scale);
            if (distance < bestDistance && Existing(dir.Path, icon) is { } candidate)
            {
                best = candidate;
                bestDistance = distance;
            }
        }
        return best;
    }

    string? FindUnthemed(string icon)
    {
        foreach (var dir in baseDirs)
        {
            if (Existing(dir, icon) is { } found)
                return found;
        }
        return null;
    }

    static string? Existing(string dir, string icon)
    {
        foreach (var ext in Extensions)
        {
            string path = System.IO.Path.Combine(dir, icon + ext);
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    ThemeData? LoadTheme(string name)
    {
        var roots = baseDirs.Select(b => System.IO.Path.Combine(b, name)).Where(Directory.Exists).ToList();
        var index = roots.Select(r => System.IO.Path.Combine(r, "index.theme")).FirstOrDefault(File.Exists);
        if (index is null)
            return null;
        var file = KeyFile.Load(index);
        var directories = new List<ThemeDirectory>();
        foreach (var subdir in file.GetList("Icon Theme", "Directories", ',').Concat(file.GetList("Icon Theme", "ScaledDirectories", ',')).Distinct())
        {
            int size = Int(file.Get(subdir, "Size"), 0);
            if (size == 0)
                continue;
            var entry = new ThemeDirectory("", size, Int(file.Get(subdir, "Scale"), 1), file.Get(subdir, "Type") ?? "Threshold",
                Int(file.Get(subdir, "MinSize"), size), Int(file.Get(subdir, "MaxSize"), size), Int(file.Get(subdir, "Threshold"), 2));
            foreach (var root in roots)
                directories.Add(entry with { Path = System.IO.Path.Combine(root, subdir) });
        }
        return new ThemeData(name, roots, directories);
    }

    IEnumerable<string> ReadInherits(string name)
    {
        var index = baseDirs.Select(b => System.IO.Path.Combine(b, name, "index.theme")).FirstOrDefault(File.Exists);
        return index is null ? [] : KeyFile.Load(index).GetList("Icon Theme", "Inherits", ',');
    }

    static int Int(string? value, int fallback) => int.TryParse(value, out int v) ? v : fallback;

    static IReadOnlyList<string> DefaultBaseDirs()
    {
        string home = Environment.GetEnvironmentVariable("HOME") ?? "/";
        return new[] { System.IO.Path.Combine(home, ".icons") }
            .Concat(DesktopEntries.DataDirs().Select(d => System.IO.Path.Combine(d, "icons")))
            .Append("/usr/share/pixmaps")
            .Distinct()
            .ToList();
    }
}
