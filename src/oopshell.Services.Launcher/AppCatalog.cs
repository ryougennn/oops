using oopshell.Services.Desktop;

namespace oopshell.Services.Launcher;

/// <summary>Builds and runs application command lines.</summary>
public static class AppLauncher
{
    /// <summary>
    /// The command line for <paramref name="entry"/> (or one of its actions) with files/URLs; Terminal=true apps
    /// are wrapped in the user's terminal (<see cref="Terminal.Wrap"/>). Throws if no terminal is installed.
    /// </summary>
    public static IReadOnlyList<string> CommandLine(DesktopEntry entry, DesktopAction? action = null, IReadOnlyList<string>? filesOrUrls = null, string? terminal = null)
    {
        var argv = action is null ? entry.ExpandExec(filesOrUrls) : entry.ExpandExec(action, filesOrUrls);
        if (argv.Count == 0)
            throw new InvalidOperationException($"{entry.Id} has an empty Exec line.");
        if (!entry.Terminal)
            return argv;
        return Terminal.Wrap(argv, terminal) ?? throw new InvalidOperationException($"{entry.Name} needs a terminal, and none is installed (set $TERMINAL).");
    }

    /// <summary>Starts the application detached, with an activation token for <paramref name="from"/>.</summary>
    public static Task LaunchAsync(OopsApp app, DesktopEntry entry, DesktopAction? action = null, IReadOnlyList<string>? filesOrUrls = null,
        Surface? from = null, string? terminal = null)
    {
        var argv = CommandLine(entry, action, filesOrUrls, terminal);
        string? workingDirectory = entry.WorkingDirectory is { } dir && Directory.Exists(dir) ? dir : null;
        string appId = entry.Id.EndsWith(".desktop", StringComparison.Ordinal) ? entry.Id[..^8] : entry.Id;
        return app.LaunchAsync(argv[0], argv.Skip(1), new LaunchOptions { Surface = from, AppId = appId, WorkingDirectory = workingDirectory });
    }
}

/// <summary>Options for <see cref="AppCatalog.Start"/>.</summary>
public sealed record AppCatalogOptions
{
    /// <summary>Data dirs to read (default: <c>$XDG_DATA_HOME</c> then <c>$XDG_DATA_DIRS</c>).</summary>
    public IReadOnlyList<string>? DataDirs { get; init; }
    /// <summary>Locale for names (default: LC_ALL, LC_MESSAGES, LANG).</summary>
    public string? Locale { get; init; }
    /// <summary>Desktops for OnlyShowIn/NotShowIn (default: <c>$XDG_CURRENT_DESKTOP</c>).</summary>
    public IReadOnlyList<string>? CurrentDesktops { get; init; }
    /// <summary>Launch history for ranking (default: <see cref="UsageHistory.Load"/>).</summary>
    public UsageHistory? Usage { get; init; }
    /// <summary>The terminal for Terminal=true apps (default: <see cref="Terminal.Wrap"/>'s choice).</summary>
    public string? Terminal { get; init; }
    /// <summary>Watch the application directories and reload on changes (default true).</summary>
    public bool Watch { get; init; } = true;
}

/// <summary>
/// The installed applications, kept up to date: everything a launcher, dock or taskbar needs.
/// <code>
/// using var apps = AppCatalog.Start(app);
/// foreach (var match in apps.Search("fire")) …
/// await apps.LaunchAsync(match.Entry, match.Action, from: surface);
/// await apps.OpenAsync("/home/me/report.pdf");      // the user's default PDF viewer
/// </code>
/// Reloads (on the UI thread, then <see cref="Changed"/>) when .desktop files are added, changed or removed.
/// Hidden, NoDisplay, other desktops' and uninstalled (TryExec) entries are left out of <see cref="Apps"/>
/// but still used to open files.
/// </summary>
public sealed class AppCatalog : IDisposable
{
    private readonly OopsApp _app;
    private readonly AppCatalogOptions _options;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Dictionary<string, DesktopEntry?> _byAppId = new();
    private MimeDatabase? _mimeDatabase;
    private IDisposable? _reloadTimer;
    private bool _disposed;

    private AppCatalog(OopsApp app, AppCatalogOptions options)
    {
        _app = app;
        _options = options;
        Usage = options.Usage ?? UsageHistory.Load();
        Reload();
    }

    public static AppCatalog Start(OopsApp app, AppCatalogOptions? options = null) => new(app, options ?? new AppCatalogOptions());

    /// <summary>Applications to show, sorted by name.</summary>
    public IReadOnlyList<DesktopEntry> Apps { get; private set; } = [];

    /// <summary>Every parsed entry, including hidden handlers (for <see cref="OpenAsync"/>).</summary>
    public IReadOnlyList<DesktopEntry> AllEntries { get; private set; } = [];

    public UsageHistory Usage { get; }

    /// <summary>The applications changed (on the UI thread).</summary>
    public event Action? Changed;

    private IReadOnlyList<string> DataDirs => _options.DataDirs ?? DesktopEntries.DataDirs().ToList();

    public DesktopEntry? ById(string desktopId) => AllEntries.FirstOrDefault(e => e.Id == desktopId);

    /// <summary>The application a window belongs to (<see cref="DesktopEntries.FindByAppId"/>), cached.</summary>
    public DesktopEntry? FindByAppId(string appId)
    {
        if (!_byAppId.TryGetValue(appId, out var entry))
            _byAppId[appId] = entry = DesktopEntries.FindByAppId(AllEntries.Where(e => !e.Hidden).ToList(), appId);
        return entry;
    }

    /// <summary>Ranked matches among <see cref="Apps"/> (and their actions), boosted by usage.</summary>
    public List<AppMatch> Search(string query, int max = 20) => AppSearch.Search(Apps, query, id => Usage.Boost(id), max);

    /// <summary>Launches an application (or action), with files/URLs, and records it for ranking.</summary>
    public async Task LaunchAsync(DesktopEntry entry, DesktopAction? action = null, IReadOnlyList<string>? filesOrUrls = null, Surface? from = null)
    {
        await AppLauncher.LaunchAsync(_app, entry, action, filesOrUrls, from, _options.Terminal);
        Usage.RecordLaunch(entry.Id);
    }

    /// <summary>
    /// Opens a file, folder or URL with the user's default application (mimeapps.list). Falls back to
    /// <c>xdg-open</c> when no application claims the type. Returns the application used, if any.
    /// </summary>
    public async Task<DesktopEntry?> OpenAsync(string target, Surface? from = null)
    {
        // A relative path becomes absolute, so a file named "-rf" reaches the app as a file, not an option.
        if (MimeDatabase.TryScheme(target) is null && !Path.IsPathRooted(target))
            target = Path.GetFullPath(target);
        _mimeDatabase ??= MimeDatabase.Load(DataDirs);
        var handler = DefaultApps.For(_mimeDatabase.Guess(target), AllEntries, _mimeDatabase, DefaultApps.ListFiles(_options.CurrentDesktops, DataDirs));
        if (handler is null)
        {
            await _app.LaunchAsync("xdg-open", [target], new LaunchOptions { Surface = from });
            return null;
        }
        // %f wants a path; %u takes the URL as is.
        string argument = target.StartsWith("file://", StringComparison.OrdinalIgnoreCase) && handler.Exec?.Contains("%u", StringComparison.OrdinalIgnoreCase) != true
            ? MimeDatabase.FilePath(target) ?? throw new ArgumentException($"Not a valid file URL: {target}", nameof(target))
            : target;
        await AppLauncher.LaunchAsync(_app, handler, null, [argument], from, _options.Terminal);
        return handler;
    }

    private void Reload()
    {
        var dirs = DataDirs;
        AllEntries = DesktopEntries.LoadAll(_options.Locale, dirs);
        var desktops = _options.CurrentDesktops;
        Apps = AllEntries.Where(e => e.IsVisible(desktops) && (e.TryExec is null || Terminal.FindOnPath(e.TryExec) is not null)).ToList();
        _byAppId.Clear();
        _mimeDatabase = null;
        if (_options.Watch)
            Rewatch(dirs);
    }

    private void Rewatch(IReadOnlyList<string> dirs)
    {
        foreach (var watcher in _watchers)
            watcher.Dispose();
        _watchers.Clear();
        foreach (var dir in dirs)
        {
            string applications = Path.Combine(dir, "applications");
            // Watch the applications dir, or its parent until it appears (e.g. a first per-user install).
            var (path, filter, recursive) = Directory.Exists(applications) ? (applications, "*.desktop", true)
                : Directory.Exists(dir) ? (dir, "applications", false)
                : (null, "", false);
            if (path is null)
                continue;
            try
            {
                var watcher = new FileSystemWatcher(path, filter)
                {
                    IncludeSubdirectories = recursive,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                };
                watcher.Created += OnChanged;
                watcher.Deleted += OnChanged;
                watcher.Changed += OnChanged;
                watcher.Renamed += OnChanged;
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Out of inotify watches or unreadable: that directory is simply not watched.
            }
        }
    }

    /// <summary>Watcher events come from a worker thread and in bursts (package installs): reload once, 300 ms after the last.</summary>
    private void OnChanged(object sender, FileSystemEventArgs e) => _app.Post(() =>
    {
        if (_disposed)
            return;
        _reloadTimer?.Dispose();
        _reloadTimer = _app.SetTimer(TimeSpan.FromMilliseconds(300), null, () =>
        {
            _reloadTimer = null;
            if (_disposed)
                return;
            Reload();
            Changed?.Invoke();
        });
    });

    public void Dispose()
    {
        _disposed = true;
        _reloadTimer?.Dispose();
        foreach (var watcher in _watchers)
            watcher.Dispose();
        _watchers.Clear();
    }
}
