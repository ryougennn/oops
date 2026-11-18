using oopshell.Services.Desktop;
using oopshell.Services.Launcher;

namespace oopshell.Services.Tests;

public sealed class LauncherTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("oopslaunch.").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    static DesktopEntry Entry(string id, string body, string? locale = null) =>
        DesktopEntry.Parse(id, "/x/" + id, "[Desktop Entry]\nType=Application\n" + body, locale)!;

    string Write(string relativePath, string content)
    {
        string path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    // ---- folding and search --------------------------------------------------------------------

    [Theory]
    [InlineData("Café Ångström", "cafe angstrom")]
    [InlineData("ＦＩＲＥ　Ｆｏｘ", "fire fox")]
    [InlineData("ふぁいあ", "ファイア")]
    [InlineData("Straße Œuvre", "strasse oeuvre")]
    public void Text_folds_case_accents_width_and_kana(string text, string folded) =>
        Assert.Equal(folded, TextFold.Fold(text));

    static readonly DesktopEntry[] Apps =
    [
        Entry("org.mozilla.firefox.desktop", "Name=Firefox\nGenericName=Web Browser\nKeywords=Internet;WWW;\nExec=firefox %u\nActions=new-window;new-private-window;\n\n[Desktop Action new-window]\nName=New Window\nExec=firefox --new-window\n\n[Desktop Action new-private-window]\nName=New Private Window\nExec=firefox --private-window"),
        Entry("libreoffice-writer.desktop", "Name=LibreOffice Writer\nGenericName=Word Processor\nKeywords=Text;Letter;Document;\nExec=libreoffice --writer %U"),
        Entry("libreoffice-calc.desktop", "Name=LibreOffice Calc\nGenericName=Spreadsheet\nExec=libreoffice --calc %U"),
        Entry("firewall-config.desktop", "Name=Firewall\nName[ja]=ファイアウォール\nExec=firewall-config", "ja_JP.UTF-8"),
        Entry("cafe.desktop", "Name=Café Notes\nExec=cafe-notes"),
        Entry("org.gnome.Nautilus.desktop", "Name=Files\nKeywords=folder;manager;\nExec=nautilus --new-window %U"),
        Entry("org.kde.konsole.desktop", "Name=Konsole\nGenericName=Terminal\nExec=konsole"),
        Entry("steam.desktop", "Name=Steam\nExec=/usr/bin/steam %U"),
    ];

    static string[] Top(string query, int n = 1, Func<string, double>? boost = null) =>
        AppSearch.Search(Apps, query, boost).Take(n).Select(m => m.Label).ToArray();

    [Fact]
    public void Search_ranks_the_way_people_type()
    {
        Assert.Equal(["Firefox", "ファイアウォール"], Top("fire", 2));    // name prefix, then the untranslated name
        Assert.Equal(["Firefox"], Top("ffx"));                           // letters in order
        Assert.Equal(["Firefox"], Top("ＦＩＲＥ"));                       // full-width
        Assert.Equal(["Firefox"], Top("web"));                           // generic name
        Assert.Equal(["LibreOffice Writer"], Top("lo wri"));             // word starts
        Assert.Equal(["LibreOffice Writer"], Top("low"));                // initials
        Assert.Equal(["LibreOffice Calc"], Top("spreadsheet"));
        Assert.Equal(["LibreOffice Writer"], Top("letter"));             // keyword
        Assert.Equal(["Café Notes"], Top("cafe"));                       // accents
        Assert.Equal(["ファイアウォール"], Top("ふぁいあ"));                 // hiragana finds katakana
        Assert.Equal(["Files"], Top("nautilus"));                        // the program
        Assert.Equal(["Files"], Top("folder"));
        Assert.Empty(AppSearch.Search(Apps, "   "));
        Assert.Empty(AppSearch.Search(Apps, "zzzz"));
    }

    [Fact]
    public void Actions_are_found_and_usage_breaks_ties()
    {
        Assert.Equal(["Firefox — New Private Window"], Top("private"));
        Assert.Equal(["Firefox — New Private Window"], Top("firefox priv"));
        // A translated action is still found by its English name.
        var japanese = Entry("firefox.desktop", "Name=Firefox\nExec=firefox\nActions=private;\n\n[Desktop Action private]\nName=New Private Window\nName[ja]=新しいプライベートウィンドウ\nExec=firefox --private-window", "ja_JP.UTF-8");
        Assert.Equal(["Firefox — 新しいプライベートウィンドウ"], AppSearch.Search([japanese], "private").Select(m => m.Label));
        var match = AppSearch.Search(Apps, "private")[0];
        Assert.Equal(["firefox", "--private-window"], match.Entry.ExpandExec(match.Action!));

        // "libreoffice" matches Writer and Calc equally; usage decides.
        Assert.Equal("LibreOffice Calc", Top("libreoffice")[0]);         // alphabetical without usage
        var usage = UsageHistory.InMemory();
        for (int i = 0; i < 3; i++)
            usage.RecordLaunch("libreoffice-writer.desktop");
        Assert.Equal("LibreOffice Writer", Top("libreoffice", boost: id => usage.Boost(id))[0]);
    }

    [Fact]
    public void Usage_fades_and_survives_restarts_and_corruption()
    {
        var now = DateTimeOffset.UtcNow;
        var usage = UsageHistory.InMemory();
        usage.RecordLaunch("a.desktop", now.AddDays(-60));
        usage.RecordLaunch("b.desktop", now);
        Assert.True(usage.Boost("b.desktop", now) > usage.Boost("a.desktop", now));
        Assert.Equal(0, usage.Boost("never.desktop", now));

        string path = Path.Combine(root, "state", "usage.json");
        UsageHistory.Load(path).RecordLaunch("kept.desktop");
        Assert.True(UsageHistory.Load(path).Boost("kept.desktop") > 0);
        File.WriteAllText(path, "{ not json");
        Assert.Equal(0, UsageHistory.Load(path).Boost("kept.desktop"));
    }

    // ---- what distributions actually ship -------------------------------------------------------

    [Fact]
    public void Real_world_layouts_load_with_their_quirks()
    {
        string home = Path.Combine(root, "home/.local/share");
        string flatpak = Path.Combine(root, "var/lib/flatpak/exports/share");
        string snap = Path.Combine(root, "var/lib/snapd/desktop");
        string system = Path.Combine(root, "usr/share");
        string nixProfile = Path.Combine(root, "home/.nix-profile/share");
        string nixStore = Path.Combine(root, "nix/store/abc123-hello-2.12/share");

        // A user override hides a system entry; a subdirectory makes a dashed id.
        Write("usr/share/applications/org.example.Hidden.desktop", "[Desktop Entry]\nType=Application\nName=Should Hide\nExec=hidden");
        Write("home/.local/share/applications/org.example.Hidden.desktop", "[Desktop Entry]\nType=Application\nName=Should Hide\nExec=hidden\nHidden=true");
        Write("usr/share/applications/kde4/old.desktop", "[Desktop Entry]\nType=Application\nName=Old KDE App\nExec=old");
        // Flatpak exports, with its file-forwarding markers.
        Write("var/lib/flatpak/exports/share/applications/org.mozilla.firefox.desktop",
            "[Desktop Entry]\nType=Application\nName=Firefox\nExec=/usr/bin/flatpak run --branch=stable --arch=x86_64 --command=firefox --file-forwarding org.mozilla.firefox @@u %u @@\nStartupWMClass=firefox");
        // Snap, behind env with a variable.
        Write("var/lib/snapd/desktop/applications/vlc_vlc.desktop",
            "[Desktop Entry]\nType=Application\nName=VLC media player\nExec=env BAMF_DESKTOP_FILE_HINT=/var/lib/snapd/desktop/applications/vlc_vlc.desktop /snap/bin/vlc --started-from-file %U");
        // NixOS: the profile's files are symlinks into the store, and whole directories can be too.
        string storeFile = Write("nix/store/abc123-hello-2.12/share/applications/hello.desktop", "[Desktop Entry]\nType=Application\nName=Hello\nExec=/nix/store/abc123-hello-2.12/bin/hello");
        Directory.CreateDirectory(Path.Combine(nixProfile, "applications"));
        File.CreateSymbolicLink(Path.Combine(nixProfile, "applications", "hello.desktop"), storeFile);
        Write("nix/store/def456-world/share/applications/world.desktop", "[Desktop Entry]\nType=Application\nName=World\nExec=world");
        Directory.CreateSymbolicLink(Path.Combine(nixProfile, "applications", "linked"), Path.Combine(root, "nix/store/def456-world/share/applications"));
        // Steam game, AppImage with a space, BOM and CRLF, a missing TryExec, another desktop only.
        Write("home/.local/share/applications/steam_app_620.desktop", "[Desktop Entry]\nType=Application\nName=Portal 2\nExec=steam steam://rungameid/620\nIcon=steam_icon_620");
        Write("home/.local/share/applications/myapp.desktop", "[Desktop Entry]\nType=Application\nName=My App\nExec=\"/home/u/Apps/My App.AppImage\" --no-sandbox %U");
        Write("usr/share/applications/bom.desktop", "﻿[Desktop Entry]\r\nType=Application\r\nName=With BOM\r\nExec=bom\r\n");
        Write("usr/share/applications/gone.desktop", "[Desktop Entry]\nType=Application\nName=Uninstalled\nExec=gone\nTryExec=/definitely/not/here/gone");
        Write("usr/share/applications/gnome-only.desktop", "[Desktop Entry]\nType=Application\nName=GNOME Only\nExec=g\nOnlyShowIn=GNOME;");
        Write("usr/share/applications/broken.desktop", "this is not a desktop file");

        var dirs = new[] { home, nixProfile, flatpak, snap, system, nixStore };
        var all = DesktopEntries.LoadAll("C", dirs);
        var visible = all.Where(e => e.IsVisible(["KDE"]) && (e.TryExec is null || Terminal.FindOnPath(e.TryExec) is not null)).ToList();
        Assert.Equal(
            ["Firefox", "Hello", "My App", "Old KDE App", "Portal 2", "VLC media player", "With BOM", "World"],
            visible.Select(e => e.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Contains(all, e => e.Id == "kde4-old.desktop");
        Assert.Contains(all, e => e.Id == "linked-world.desktop");

        var firefox = visible.Single(e => e.Name == "Firefox");
        Assert.Equal(["/usr/bin/flatpak", "run", "--branch=stable", "--arch=x86_64", "--command=firefox", "--file-forwarding", "org.mozilla.firefox", "@@u", "/tmp/a.html", "@@"],
            firefox.ExpandExec(["/tmp/a.html"]));
        Assert.Same(firefox, DesktopEntries.FindByAppId(visible, "firefox"));
        var vlc = visible.Single(e => e.Name == "VLC media player");
        Assert.Equal("env", vlc.ExpandExec()[0]);
        Assert.Equal(["VLC media player"], AppSearch.Search(visible, "vlc").Take(1).Select(m => m.Label));
        Assert.Equal("/home/u/Apps/My App.AppImage", visible.Single(e => e.Name == "My App").ExpandExec()[0]);
        Assert.Equal(["steam", "steam://rungameid/620"], visible.Single(e => e.Name == "Portal 2").ExpandExec());
    }

    // ---- terminals ---------------------------------------------------------------------------

    string FakePath(params string[] programs)
    {
        string bin = Path.Combine(root, "bin-" + string.Join("-", programs));
        Directory.CreateDirectory(bin);
        foreach (var program in programs)
        {
            string path = Path.Combine(bin, program);
            File.WriteAllText(path, "#!/bin/sh\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
        return bin;
    }

    [Fact]
    public void Terminal_apps_run_in_the_users_terminal_with_its_conventions()
    {
        string[] htop = ["htop"];
        Assert.Equal(["konsole", "-e", "htop"], Terminal.Wrap(htop, pathVariable: FakePath("konsole")));
        Assert.Equal(["gnome-terminal", "--", "htop"], Terminal.Wrap(htop, pathVariable: FakePath("gnome-terminal")));
        Assert.Equal(["foot", "htop"], Terminal.Wrap(htop, pathVariable: FakePath("foot", "xterm")));
        Assert.Equal(["xdg-terminal-exec", "htop"], Terminal.Wrap(htop, pathVariable: FakePath("xdg-terminal-exec", "konsole")));
        Assert.Equal(["kitty", "-1", "htop"], Terminal.Wrap(htop, preferred: "kitty -1", pathVariable: FakePath("kitty", "konsole")));
        Assert.Equal(["xterm", "-e", "htop"], Terminal.Wrap(htop, preferred: "not-installed", pathVariable: FakePath("xterm")));
        Assert.Null(Terminal.Wrap(htop, pathVariable: FakePath()));

        var top = Entry("top.desktop", "Name=top\nExec=top -d 1\nTerminal=true");
        string alacritty = Path.Combine(FakePath("alacritty"), "alacritty"); // a path also works, conventions by name
        Assert.Equal([alacritty, "-e", "top", "-d", "1"], AppLauncher.CommandLine(top, terminal: alacritty));
    }

    // ---- MIME types and default applications --------------------------------------------------------

    [Fact]
    public void Files_folders_and_urls_open_with_the_users_defaults()
    {
        string data = Path.Combine(root, "share");
        Write("share/mime/globs2", "# comment\n50:text/plain:*.txt\n50:application/pdf:*.pdf\n50:text/x-csrc:*.c\n50:application/gzip:*.gz\n55:application/x-compressed-tar:*.tar.gz\n50:image/png:*.png\n50:text/x-makefile:Makefile:cs\n");
        Write("share/mime/subclasses", "text/x-csrc text/plain\napplication/x-compressed-tar application/gzip\n");
        Write("share/mime/aliases", "application/x-pdf application/pdf\n");
        var mime = MimeDatabase.Load([data]);
        Assert.Equal("application/pdf", mime.FromFileName("Report.PDF"));
        Assert.Equal("application/x-compressed-tar", mime.FromFileName("src.tar.gz"));
        Assert.Equal("application/gzip", mime.FromFileName("log.gz"));
        Assert.Equal("text/x-makefile", mime.FromFileName("Makefile"));
        Assert.Equal("application/octet-stream", mime.FromFileName("makefile")); // that glob is case-sensitive
        Assert.Equal("application/octet-stream", mime.FromFileName("noext"));
        Assert.Equal("inode/directory", mime.Guess(root));
        Assert.Equal("x-scheme-handler/https", mime.Guess("https://example.org/a.pdf"));
        Assert.Equal("text/plain", mime.Guess("file:///tmp/notes%20a.txt"));
        Assert.Equal(["text/x-csrc", "text/plain", "application/octet-stream"], mime.WithParents("text/x-csrc"));
        Assert.Equal("application/pdf", mime.WithParents("application/x-pdf").First());

        DesktopEntry[] apps =
        [
            Entry("org.gnome.Evince.desktop", "Name=Document Viewer\nExec=evince %U\nMimeType=application/pdf;"),
            Entry("okular.desktop", "Name=Okular\nExec=okular %U\nMimeType=application/pdf;"),
            Entry("kwrite.desktop", "Name=KWrite\nExec=kwrite %U\nMimeType=text/plain;"),
            Entry("kate.desktop", "Name=Kate\nExec=kate %U\nMimeType=text/plain;"),
            Entry("firefox.desktop", "Name=Firefox\nExec=firefox %u\nMimeType=x-scheme-handler/https;"),
            Entry("gimp.desktop", "Name=GIMP\nExec=gimp %U\nMimeType=image/png;"),
            Entry("ark.desktop", "Name=Ark\nExec=ark %U\nMimeType=application/gzip;\nNoDisplay=true"),
        ];
        string general = Write("config/mimeapps.list",
            "[Default Applications]\napplication/pdf=missing.desktop;okular.desktop;\ntext/plain=kwrite.desktop\n\n[Removed Associations]\nimage/png=gimp.desktop;\n");
        string kde = Write("config/kde-mimeapps.list", "[Default Applications]\ntext/plain=kate.desktop\n");

        string? For(string type, params string[] lists) => DefaultApps.For(type, apps, mime, lists)?.Id;
        Assert.Equal("okular.desktop", For("application/pdf", general));               // skips the uninstalled default
        Assert.Equal("org.gnome.Evince.desktop", For("application/pdf"));                // no list: a declaring app
        Assert.Equal("kate.desktop", For("text/plain", kde, general));                   // the desktop's own list first
        Assert.Equal("kwrite.desktop", For("text/x-csrc", general));                     // via the parent type
        Assert.Null(For("image/png", general));                                         // the only handler was removed
        Assert.Equal("ark.desktop", For("application/x-compressed-tar", general));      // NoDisplay handlers count
        Assert.Equal("firefox.desktop", For("x-scheme-handler/https", general));

        var lists = DefaultApps.ListFiles(["KDE"], [data]);
        Assert.EndsWith("kde-mimeapps.list", lists[0]);
        Assert.EndsWith("mimeapps.list", lists[1]);
        Assert.EndsWith(Path.Combine("share", "applications", "mimeapps.list"), lists[^1]);
    }
}
