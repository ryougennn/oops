using System.Text;

namespace oopshell.Services.Desktop;

/// <summary>An application from a <c>.desktop</c> file (Desktop Entry Specification 1.5).</summary>
public sealed class DesktopEntry
{
    const string Group = "Desktop Entry";

    /// <summary>The desktop file id, e.g. <c>org.gnome.Nautilus.desktop</c> (subdirectories joined with '-').</summary>
    public required string Id { get; init; }
    public required string Path { get; init; }
    public string Type { get; init; } = "Application";
    public required string Name { get; init; }
    public string? GenericName { get; init; }
    public string? Comment { get; init; }
    public string? Exec { get; init; }
    public string? TryExec { get; init; }
    public string? WorkingDirectory { get; init; }
    public string? Icon { get; init; }
    public IReadOnlyList<string> Categories { get; init; } = [];
    public IReadOnlyList<string> Keywords { get; init; } = [];
    public IReadOnlyList<string> OnlyShowIn { get; init; } = [];
    public IReadOnlyList<string> NotShowIn { get; init; } = [];
    public bool NoDisplay { get; init; }
    public bool Hidden { get; init; }
    public bool Terminal { get; init; }
    /// <summary>The window class (Wayland app_id) the application's windows use, if the file says.</summary>
    public string? StartupWMClass { get; init; }
    /// <summary>The untranslated Name (equal to <see cref="Name"/> without a translation), for searching in either language.</summary>
    public string DefaultName { get; init; } = "";
    /// <summary>The untranslated Keywords.</summary>
    public IReadOnlyList<string> DefaultKeywords { get; init; } = [];
    /// <summary>MIME types the application opens.</summary>
    public IReadOnlyList<string> MimeTypes { get; init; } = [];
    /// <summary>The application can be started over D-Bus (its Exec still works as a fallback).</summary>
    public bool DBusActivatable { get; init; }
    /// <summary>Extra entry points ("New Private Window"), from the Actions key and [Desktop Action …] groups.</summary>
    public IReadOnlyList<DesktopAction> Actions { get; init; } = [];

    /// <summary>Parses a desktop file; returns null if it has no [Desktop Entry] group or no Name.</summary>
    public static DesktopEntry? Parse(string id, string path, string text, string? locale = null)
    {
        var file = KeyFile.Parse(text);
        if (file.GetLocalized(Group, "Name", locale) is not { } name)
            return null;
        var actions = new List<DesktopAction>();
        foreach (var actionId in file.GetList(Group, "Actions"))
        {
            string group = $"Desktop Action {actionId}";
            if (file.GetLocalized(group, "Name", locale) is { } actionName)
                actions.Add(new DesktopAction(actionId, actionName, file.Get(group, "Exec"), file.GetLocalized(group, "Icon", locale), file.Get(group, "Name")));
        }
        return new DesktopEntry
        {
            Id = id,
            Path = path,
            Type = file.Get(Group, "Type") ?? "Application",
            Name = name,
            GenericName = file.GetLocalized(Group, "GenericName", locale),
            Comment = file.GetLocalized(Group, "Comment", locale),
            Exec = file.Get(Group, "Exec"),
            TryExec = file.Get(Group, "TryExec"),
            WorkingDirectory = file.Get(Group, "Path"),
            Icon = file.GetLocalized(Group, "Icon", locale),
            Categories = file.GetList(Group, "Categories"),
            Keywords = file.GetList(Group, "Keywords"),
            OnlyShowIn = file.GetList(Group, "OnlyShowIn"),
            NotShowIn = file.GetList(Group, "NotShowIn"),
            NoDisplay = file.GetBool(Group, "NoDisplay"),
            Hidden = file.GetBool(Group, "Hidden"),
            Terminal = file.GetBool(Group, "Terminal"),
            StartupWMClass = file.Get(Group, "StartupWMClass"),
            DefaultName = file.Get(Group, "Name") ?? name,
            DefaultKeywords = file.GetList(Group, "Keywords"),
            MimeTypes = file.GetList(Group, "MimeType"),
            DBusActivatable = file.GetBool(Group, "DBusActivatable"),
            Actions = actions,
        };
    }

    /// <summary>
    /// Whether a menu should list this entry: an application, not hidden or NoDisplay, allowed in the current
    /// desktop (<c>XDG_CURRENT_DESKTOP</c>, ':'-separated, or <paramref name="currentDesktops"/>).
    /// </summary>
    public bool IsVisible(IEnumerable<string>? currentDesktops = null)
    {
        if (Type != "Application" || Hidden || NoDisplay || Exec is null)
            return false;
        var desktops = (currentDesktops ?? (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)).ToList();
        if (OnlyShowIn.Count > 0 && !OnlyShowIn.Intersect(desktops).Any())
            return false;
        return !NotShowIn.Intersect(desktops).Any();
    }

    /// <summary>
    /// The command line to run, with field codes expanded (<c>%f %F %u %U %i %c %k %%</c>; deprecated codes
    /// are dropped). The first element is the program.
    /// </summary>
    public IReadOnlyList<string> ExpandExec(IReadOnlyList<string>? filesOrUrls = null) =>
        ExpandExec(Exec ?? throw new InvalidOperationException($"{Id} has no Exec key."), filesOrUrls);

    /// <summary>Expands an action's command line like <see cref="ExpandExec(IReadOnlyList{string}?)"/>.</summary>
    public IReadOnlyList<string> ExpandExec(DesktopAction action, IReadOnlyList<string>? filesOrUrls = null) =>
        ExpandExec(action.Exec ?? Exec ?? throw new InvalidOperationException($"{Id} action {action.Id} has no Exec key."), filesOrUrls);

    IReadOnlyList<string> ExpandExec(string exec, IReadOnlyList<string>? filesOrUrls)
    {
        filesOrUrls ??= [];
        var result = new List<string>();
        foreach (var arg in SplitExec(exec))
        {
            switch (arg)
            {
                case "%f" or "%u":
                    if (filesOrUrls.Count > 0)
                        result.Add(filesOrUrls[0]);
                    continue;
                case "%F" or "%U":
                    result.AddRange(filesOrUrls);
                    continue;
                case "%i":
                    if (Icon is not null)
                    {
                        result.Add("--icon");
                        result.Add(Icon);
                    }
                    continue;
            }
            var expanded = new StringBuilder();
            for (int i = 0; i < arg.Length; i++)
            {
                if (arg[i] != '%' || i + 1 == arg.Length)
                {
                    expanded.Append(arg[i]);
                    continue;
                }
                char code = arg[++i];
                switch (code)
                {
                    case '%': expanded.Append('%'); break;
                    case 'c': expanded.Append(Name); break;
                    case 'k': expanded.Append(Path); break;
                    case 'f' or 'u' when filesOrUrls.Count > 0: expanded.Append(filesOrUrls[0]); break;
                    default: break; // %d %D %n %N %v %m are deprecated; unknown codes are dropped
                }
            }
            result.Add(expanded.ToString());
        }
        return result;
    }

    /// <summary>Splits an Exec value into arguments following the spec's quoting rules.</summary>
    internal static List<string> SplitExec(string exec)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false, hasArg = false;
        for (int i = 0; i < exec.Length; i++)
        {
            char c = exec[i];
            if (inQuotes)
            {
                if (c == '"')
                    inQuotes = false;
                else if (c == '\\' && i + 1 < exec.Length && exec[i + 1] is '"' or '`' or '$' or '\\')
                    current.Append(exec[++i]);
                else
                    current.Append(c);
            }
            else if (c == '"')
            {
                inQuotes = true;
                hasArg = true;
            }
            else if (c is ' ' or '\t')
            {
                if (hasArg)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    hasArg = false;
                }
            }
            else
            {
                current.Append(c);
                hasArg = true;
            }
        }
        if (hasArg)
            args.Add(current.ToString());
        return args;
    }

    public override string ToString() => $"{Id}: {Name}";
}

/// <summary>An additional entry point of an application (desktop file actions, e.g. "New Private Window").</summary>
/// <param name="Id">The action's id in the desktop file.</param>
/// <param name="Name">The (localized) name to show.</param>
/// <param name="Exec">Its command line, if it has its own.</param>
/// <param name="Icon">Its icon, if it has its own.</param>
/// <param name="DefaultName">The untranslated name, for searching in either language.</param>
public sealed record DesktopAction(string Id, string Name, string? Exec, string? Icon, string? DefaultName = null);

/// <summary>Finds installed applications.</summary>
public static class DesktopEntries
{
    /// <summary>
    /// Every desktop entry in <c>$XDG_DATA_HOME/applications</c> and <c>$XDG_DATA_DIRS/*/applications</c>.
    /// When several directories define the same desktop file id, the earlier one (user data home first) wins.
    /// </summary>
    public static IReadOnlyList<DesktopEntry> LoadAll(string? locale = null, IEnumerable<string>? dataDirs = null)
    {
        locale ??= CurrentLocale();
        var byId = new Dictionary<string, DesktopEntry>();
        foreach (var dir in dataDirs ?? DataDirs())
        {
            string apps = System.IO.Path.Combine(dir, "applications");
            if (!Directory.Exists(apps))
                continue;
            foreach (var file in Directory.EnumerateFiles(apps, "*.desktop", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                string id = System.IO.Path.GetRelativePath(apps, file).Replace('/', '-');
                if (byId.ContainsKey(id))
                    continue;
                try
                {
                    if (DesktopEntry.Parse(id, file, File.ReadAllText(file), locale) is { } entry)
                        byId[id] = entry;
                }
                catch (IOException)
                {
                    // Unreadable files are skipped, like every other desktop does.
                }
            }
        }
        return byId.Values.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// The application a window belongs to, from its Wayland app_id (what docks and taskbars need for names
    /// and icons). Tries, in order: the desktop file id, <c>StartupWMClass</c>, the last part of a reverse-DNS
    /// id (<c>org.mozilla.firefox</c> ↔ <c>firefox</c>), the program in Exec, then the Name; case-insensitive
    /// after the exact id. Entries that are not NoDisplay win ties. Null if nothing matches.
    /// </summary>
    public static DesktopEntry? FindByAppId(IEnumerable<DesktopEntry> entries, string appId)
    {
        if (string.IsNullOrEmpty(appId))
            return null;
        var list = entries as IReadOnlyList<DesktopEntry> ?? entries.ToList();
        static string Stem(DesktopEntry e) => e.Id.EndsWith(".desktop", StringComparison.Ordinal) ? e.Id[..^8] : e.Id;
        static string LastPart(string s) => s[(s.LastIndexOf('.') + 1)..];
        static string? Program(DesktopEntry e) =>
            e.Exec?.Split(' ', StringSplitOptions.RemoveEmptyEntries).SkipWhile(t => t == "env" || t.Contains('=')).FirstOrDefault() is { } p ? System.IO.Path.GetFileName(p.Trim('"')) : null;

        Func<DesktopEntry, bool>[] rules =
        [
            e => Stem(e) == appId,
            e => Stem(e).Equals(appId, StringComparison.OrdinalIgnoreCase),
            e => e.StartupWMClass?.Equals(appId, StringComparison.OrdinalIgnoreCase) == true,
            e => LastPart(Stem(e)).Equals(LastPart(appId), StringComparison.OrdinalIgnoreCase),
            e => Program(e)?.Equals(LastPart(appId), StringComparison.OrdinalIgnoreCase) == true,
            e => e.Name.Equals(appId, StringComparison.OrdinalIgnoreCase),
        ];
        foreach (var rule in rules)
        {
            var matches = list.Where(rule).ToList();
            if (matches.Count > 0)
                return matches.FirstOrDefault(e => !e.NoDisplay) ?? matches[0];
        }
        return null;
    }

    /// <summary><c>$XDG_DATA_HOME</c> (default <c>~/.local/share</c>) followed by <c>$XDG_DATA_DIRS</c> (default <c>/usr/local/share:/usr/share</c>).</summary>
    public static IEnumerable<string> DataDirs()
    {
        string home = Environment.GetEnvironmentVariable("HOME") ?? "/";
        string dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } h ? h : System.IO.Path.Combine(home, ".local/share");
        string dataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS") is { Length: > 0 } d ? d : "/usr/local/share:/usr/share";
        return new[] { dataHome }.Concat(dataDirs.Split(':', StringSplitOptions.RemoveEmptyEntries)).Distinct();
    }

    /// <summary>LC_ALL, LC_MESSAGES or LANG.</summary>
    public static string? CurrentLocale() =>
        new[] { "LC_ALL", "LC_MESSAGES", "LANG" }.Select(Environment.GetEnvironmentVariable).FirstOrDefault(v => !string.IsNullOrEmpty(v));
}
