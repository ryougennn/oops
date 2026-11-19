using oopshell.Services.Desktop;

namespace oopshell.Services.Tests;

public sealed class DesktopTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("oopshell-desktop-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    string Write(string relative, string content)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Parses_localized_names_lists_and_flags()
    {
        var entry = DesktopEntry.Parse("x.desktop", "/x.desktop", """
            [Desktop Entry]
            Type=Application
            Name=Files
            Name[de]=Dateien
            Name[de_CH]=Dateie
            Comment=Browse\sfiles
            Exec=nautilus --new-window %U
            Icon=org.gnome.Nautilus
            Categories=GNOME;GTK;Utility;Core;
            Keywords=folder\;dir;manager;
            [Desktop Action new-window]
            Name=New Window
            """, "de_DE.UTF-8")!;

        Assert.Equal("Dateien", entry.Name);
        Assert.Equal("Browse files", entry.Comment);
        Assert.Equal(["GNOME", "GTK", "Utility", "Core"], entry.Categories);
        Assert.Equal(["folder;dir", "manager"], entry.Keywords);
        Assert.Equal("Dateie", DesktopEntry.Parse("x", "x", "[Desktop Entry]\nName=a\nName[de_CH]=Dateie\n", "de_CH@euro")!.Name);
    }

    [Theory]
    [InlineData("foo %f", new[] { "a.txt", "b.txt" }, new[] { "foo", "a.txt" })]
    [InlineData("foo %F", new[] { "a.txt", "b.txt" }, new[] { "foo", "a.txt", "b.txt" })]
    [InlineData("foo %U --x", new string[0], new[] { "foo", "--x" })]
    // A field code inside quotes is shell-quoted (as GLib does; the spec leaves it undefined).
    [InlineData("\"/opt/my app/run\" --name=\"%c\" 100%%", new string[0], new[] { "/opt/my app/run", "--name='App'", "100%" })]
    [InlineData("run --name=%c", new string[0], new[] { "run", "--name=App" })]
    [InlineData("sh -c \"echo \\\"hi\\\" \\$HOME\"", new string[0], new[] { "sh", "-c", "echo \"hi\" $HOME" })]
    [InlineData("foo %i %d %k", new string[0], new[] { "foo", "--icon", "app-icon", "", "/apps/app.desktop" })]
    public void Expands_exec_field_codes(string exec, string[] files, string[] expected)
    {
        var entry = new DesktopEntry { Id = "app.desktop", Path = "/apps/app.desktop", Name = "App", Exec = exec, Icon = "app-icon" };
        Assert.Equal(expected, entry.ExpandExec(files));
    }

    [Fact]
    public void Visibility_honours_NoDisplay_Hidden_and_desktop_filters()
    {
        DesktopEntry Make(string extra) => DesktopEntry.Parse("a", "a", "[Desktop Entry]\nName=A\nExec=a\n" + extra)!;
        Assert.True(Make("").IsVisible(["sway"]));
        Assert.False(Make("NoDisplay=true").IsVisible(["sway"]));
        Assert.False(Make("Hidden=true").IsVisible(["sway"]));
        Assert.False(Make("OnlyShowIn=GNOME;").IsVisible(["sway"]));
        Assert.True(Make("OnlyShowIn=GNOME;sway;").IsVisible(["sway"]));
        Assert.False(Make("NotShowIn=sway;").IsVisible(["sway"]));
        Assert.False(Make("Type=Link").IsVisible(["sway"]));
    }

    [Fact]
    public void Windows_are_matched_to_their_applications_by_app_id()
    {
        DesktopEntry Make(string id, string body) => DesktopEntry.Parse(id, id, "[Desktop Entry]\n" + body)!;
        var entries = new[]
        {
            Make("org.kde.konsole.desktop", "Name=Konsole\nExec=konsole"),
            Make("org.mozilla.firefox.desktop", "Name=Firefox\nExec=/usr/lib/firefox/firefox %u"),
            Make("code.desktop", "Name=Visual Studio Code\nExec=env FOO=1 /usr/bin/code-oss %F\nStartupWMClass=Code"),
            Make("steam-game.desktop", "Name=A Game\nExec=steam steam://rungameid/1\nStartupWMClass=steam_app_1"),
            Make("gimp-helper.desktop", "Name=GIMP helper\nExec=gimp\nNoDisplay=true"),
            Make("gimp-2.desktop", "Name=GNU Image Manipulation Program\nExec=gimp %U"),
            Make("calc.desktop", "Name=Calculator\nExec=gnome-calculator"),
        };
        Assert.Equal("org.kde.konsole.desktop", DesktopEntries.FindByAppId(entries, "org.kde.konsole")?.Id);  // desktop file id
        Assert.Equal("code.desktop", DesktopEntries.FindByAppId(entries, "code")?.Id);                        // id beats StartupWMClass
        Assert.Equal("code.desktop", DesktopEntries.FindByAppId(entries, "CODE")?.Id);
        Assert.Equal("code.desktop", DesktopEntries.FindByAppId(entries, "code-oss")?.Id);                  // Exec behind env
        Assert.Equal("steam-game.desktop", DesktopEntries.FindByAppId(entries, "steam_app_1")?.Id);         // StartupWMClass
        Assert.Equal("org.mozilla.firefox.desktop", DesktopEntries.FindByAppId(entries, "firefox")?.Id);     // reverse-DNS tail
        Assert.Equal("org.kde.konsole.desktop", DesktopEntries.FindByAppId(entries, "konsole")?.Id);
        Assert.Equal("gimp-2.desktop", DesktopEntries.FindByAppId(entries, "gimp")?.Id);                    // Exec; the visible entry wins
        Assert.Equal("calc.desktop", DesktopEntries.FindByAppId(entries, "gnome-calculator")?.Id);
        Assert.Equal("calc.desktop", DesktopEntries.FindByAppId(entries, "calculator")?.Id);                // Name
        Assert.Null(DesktopEntries.FindByAppId(entries, "unknown"));
        Assert.Null(DesktopEntries.FindByAppId(entries, ""));
    }

    [Fact]
    public void LoadAll_prefers_earlier_data_dirs_and_builds_ids_from_subdirectories()
    {
        Write("home/applications/editor.desktop", "[Desktop Entry]\nName=My Editor\nExec=myedit\n");
        Write("usr/applications/editor.desktop", "[Desktop Entry]\nName=System Editor\nExec=edit\n");
        Write("usr/applications/kde/konsole.desktop", "[Desktop Entry]\nName=Konsole\nExec=konsole\n");
        Write("usr/applications/broken.desktop", "[Something Else]\nName=x\n");

        var entries = DesktopEntries.LoadAll("C", [Path.Combine(root, "home"), Path.Combine(root, "usr")]);

        Assert.Equal(["kde-konsole.desktop", "editor.desktop"], entries.Select(e => e.Id));
        Assert.Equal("My Editor", entries.Single(e => e.Id == "editor.desktop").Name);
    }

    [Fact]
    public void Icon_lookup_follows_sizes_scales_inheritance_and_fallbacks()
    {
        Write("icons/hicolor/index.theme", """
            [Icon Theme]
            Name=Hicolor
            Directories=48x48/apps,scalable/apps
            [48x48/apps]
            Size=48
            Type=Fixed
            [scalable/apps]
            Size=128
            MinSize=8
            MaxSize=512
            Type=Scalable
            """);
        Write("icons/Retro/index.theme", """
            [Icon Theme]
            Name=Retro
            Inherits=hicolor
            Directories=16x16/apps,32x32/apps,32x32@2/apps
            [16x16/apps]
            Size=16
            Type=Fixed
            [32x32/apps]
            Size=32
            Type=Threshold
            [32x32@2/apps]
            Size=32
            Scale=2
            Type=Fixed
            """);
        string small = Write("icons/Retro/16x16/apps/term.png", "");
        string medium = Write("icons/Retro/32x32/apps/term.png", "");
        string hidpi = Write("icons/Retro/32x32@2/apps/term.png", "");
        string inheritedPng = Write("icons/hicolor/48x48/apps/editor.png", "");
        string inheritedSvg = Write("icons/hicolor/scalable/apps/editor.svg", "");
        string pixmap = Write("pixmaps/legacy.png", "png"); // absolute paths must be non-empty regular files

        var theme = new IconTheme("Retro", [Path.Combine(root, "icons"), Path.Combine(root, "pixmaps")]);

        Assert.Equal(["Retro", "hicolor"], theme.ThemeNames);
        Assert.Equal(small, theme.Find("term", 16));
        Assert.Equal(medium, theme.Find("term", 31));        // within the threshold
        Assert.Equal(hidpi, theme.Find("term", 32, scale: 2));
        Assert.Equal(hidpi, theme.Find("term", 64));         // 32@2 is exactly 64 physical pixels
        Assert.Equal(medium, theme.Find("term", 40));        // closest otherwise
        Assert.Equal(inheritedPng, theme.Find("editor", 48)); // PNG before SVG
        Assert.Equal(inheritedSvg, theme.Find("editor", 256));
        Assert.Equal(pixmap, theme.Find("legacy"));
        Assert.Null(theme.Find("missing"));
        Assert.Equal(pixmap, theme.Find(pixmap));
    }
}

public class InstalledAppsTests
{
    [Fact]
    public void File_names_cannot_run_commands_through_sh_c_launchers()
    {
        // Exec=sh -c "… %f" is common despite the spec; a crafted file name must reach the program as text.
        string dir = Directory.CreateTempSubdirectory("oopsexec.").FullName;
        try
        {
            string marker = System.IO.Path.Combine(dir, "pwned");
            string file = System.IO.Path.Combine(dir, $"a'$(touch {marker})'`touch {marker}`.txt");
            var entry = new DesktopEntry { Id = "v.desktop", Path = "/v.desktop", Name = "V", Exec = "sh -c \"printf %s %f\"" };
            var argv = entry.ExpandExec([file]);
            using var sh = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(argv[0], argv.Skip(1)) { RedirectStandardOutput = true })!;
            string printed = sh.StandardOutput.ReadToEnd();
            sh.WaitForExit();
            Assert.Equal(file, printed);
            Assert.False(File.Exists(marker));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Lists_the_applications_installed_on_this_machine()
    {
        Assert.SkipUnless(Directory.Exists("/usr/share/applications"), "No system applications directory.");
        var visible = DesktopEntries.LoadAll().Where(e => e.IsVisible()).ToList();
        Assert.NotEmpty(visible);
        Assert.All(visible, e => Assert.NotEmpty(e.ExpandExec()));
    }
}
