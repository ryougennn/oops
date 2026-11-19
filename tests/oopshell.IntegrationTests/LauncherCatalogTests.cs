using oopshell.Services.Launcher;

namespace oopshell.IntegrationTests;

[Collection("sway")]
public sealed class LauncherCatalogTests(SwayFixture sway) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("oopscatalog.").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relativePath, string content)
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void The_catalog_follows_installs_and_removals()
    {
        using var app = sway.Connect();
        string system = Path.Combine(_root, "system"), user = Path.Combine(_root, "user");
        Directory.CreateDirectory(user); // no applications dir yet: the first per-user install creates it
        Write("system/applications/one.desktop", "[Desktop Entry]\nType=Application\nName=One\nExec=one");
        using var catalog = AppCatalog.Start(app, new AppCatalogOptions { DataDirs = [user, system], Usage = UsageHistory.InMemory(), CurrentDesktops = [] });
        Assert.Equal(["One"], catalog.Apps.Select(a => a.Name));
        int changes = 0;
        catalog.Changed += () => changes++;

        Write("system/applications/two.desktop", "[Desktop Entry]\nType=Application\nName=Two\nExec=two");
        app.RunUntil(() => catalog.Apps.Count == 2, what: "installed app");
        Write("user/applications/three.desktop", "[Desktop Entry]\nType=Application\nName=Three\nExec=three");
        app.RunUntil(() => catalog.Apps.Count == 3, what: "first per-user app");
        File.Delete(Path.Combine(_root, "system/applications/one.desktop"));
        app.RunUntil(() => catalog.Apps.All(a => a.Name != "One"), what: "removed app");
        Assert.True(changes >= 3);
        Assert.Equal(["Three"], catalog.Search("thr").Select(m => m.Label));
    }

    [Fact]
    public void Files_open_with_the_default_application()
    {
        using var app = sway.Connect();
        string data = Path.Combine(_root, "share");
        string output = Path.Combine(_root, "opened.txt");
        // A test-only file type, so the user's own mimeapps.list cannot interfere.
        Write("share/mime/globs2", "50:application/x-oopshell-test:*.oopstest\n");
        Write("share/applications/opener.desktop",
            // Exec quoting is double quotes only, with \" and \$ escaped (written doubled: key-file escaping first).
            "[Desktop Entry]\nType=Application\nName=Opener\nMimeType=application/x-oopshell-test;\nNoDisplay=true\n" +
            $$"""Exec=sh -c "echo \\"\\$1\\" > {{output}}" sh %f""" + "\n");
        using var catalog = AppCatalog.Start(app, new AppCatalogOptions { DataDirs = [data], Usage = UsageHistory.InMemory(), Watch = false });
        string file = Path.Combine(_root, "my file.oopstest");
        File.WriteAllText(file, "x");

        var used = app.Complete(catalog.OpenAsync(new Uri(file).AbsoluteUri));
        Assert.Equal("opener.desktop", used?.Id);
        app.RunUntil(() => File.Exists(output) && File.ReadAllText(output).Trim().Length > 0, what: "file opened");
        Assert.Equal(file, File.ReadAllText(output).Trim()); // %f got the path, not the file:// URL
    }

    [Fact]
    public void This_systems_applications_all_parse_launch_lines_and_search()
    {
        using var app = sway.Connect();
        using var catalog = AppCatalog.Start(app, new AppCatalogOptions { Usage = UsageHistory.InMemory(), Watch = false });
        Assert.SkipWhen(catalog.Apps.Count == 0, "No applications installed.");
        foreach (var entry in catalog.Apps)
        {
            Assert.NotEmpty(entry.ExpandExec());
            foreach (var action in entry.Actions.Where(a => a.Exec is not null))
                Assert.NotEmpty(entry.ExpandExec(action));
            // Every app is findable by its own name and its window's likely app id.
            Assert.Contains(catalog.Search(entry.Name, max: 50), m => m.Entry.Id == entry.Id);
            string stem = entry.Id[..^".desktop".Length];
            Assert.NotNull(catalog.FindByAppId(stem));
        }
        var mime = MimeDatabase.Load();
        Assert.SkipWhen(mime.FromFileName("x.pdf") == "application/octet-stream", "shared-mime-info is not installed.");
        Assert.Equal("application/pdf", mime.FromFileName("x.pdf"));
        Assert.Equal("inode/directory", mime.Guess("/"));
    }
}
