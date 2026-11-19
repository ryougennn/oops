// Mutation fuzzer for oopshell: parsers of files other programs write, and the servers other processes talk to.
//   dotnet run --project tests/oopshell.Fuzz -c Release -- [seconds per target] [target…]
// Any exception from a parser, any crash of a server, and any input taking over 1 s is a finding; findings
// are saved under artifacts/fuzz/ with the input that caused them.
using System.Diagnostics;
using System.Text;
using oopshell.Fuzz;
using oopshell.Services.Desktop;
using oopshell.Services.Launcher;
using static oopshell.Fuzz.Encodings;

int seconds = args.Length > 0 && int.TryParse(args[0], out int s) ? s : 20;
var only = args.Skip(1).ToHashSet();
string findingsDir = Path.Combine(Repo.Root, "artifacts", "fuzz");
Directory.CreateDirectory(findingsDir);
var work = Directory.CreateTempSubdirectory("oopsfuzz.").FullName;
var findings = new Findings(findingsDir);

string Seed(string path, int maxChars = 20000) => File.Exists(path) ? File.ReadAllText(path) is var t && t.Length > maxChars ? t[..maxChars] : t : "";
var desktopSeeds = Directory.Exists("/usr/share/applications")
    ? Directory.GetFiles("/usr/share/applications", "*.desktop").OrderBy(p => p).Take(40).Select(p => Seed(p)).Where(t => t.Length > 0).ToList()
    : [];
desktopSeeds.Add("[Desktop Entry]\nType=Application\nName=A\nName[ja]=あ\nExec=sh -c \"echo \\\\\"$1\\\\\"\" sh %F --flag=%u %i %c %k\nActions=a;b;\n\n[Desktop Action a]\nName=A\nExec=a %U\n");
var entries = desktopSeeds.Select((t, i) => DesktopEntry.Parse($"seed{i}.desktop", "/seed", t, "ja_JP.UTF-8")).OfType<DesktopEntry>().ToList();
string globs = Seed("/usr/share/mime/globs2", 4000) + "\n50:text/x-test:*.[ch]\n50:a/b:*.tar.gz:cs\n";
string subclasses = Seed("/usr/share/mime/subclasses", 3000);
string aliases = Seed("/usr/share/mime/aliases", 3000);
string indexTheme = Seed("/usr/share/icons/hicolor/index.theme", 6000);
string mimeapps = "[Default Applications]\ntext/plain=a.desktop;b.desktop;\n[Added Associations]\nimage/png=c.desktop;\n[Removed Associations]\ntext/plain=b.desktop;\n";
string usage = "{\"launches\":{\"a.desktop\":[\"2026-01-01T00:00:00Z\"]}}";
string[] tokens = ["[Desktop Entry]", "[Desktop Action x]", "Exec=", "Name[ja]=", "Actions=", "TryExec=", "\\s", "\\n", "\\\\", "\\;", "%%", "Hidden=true",
    "text/plain", "inode/directory", "*.", ":", "50:", "Directories=", "Size=", "Scale=", "Type=Scalable", "MinSize=", "Inherits=",
    "{\"launches\":", "[", "]", "\"", "null", "1e999", "PAM_PROMPT_ECHO_OFF ", "\\101", "\\777", "\\"];

var targets = new List<(string Name, Action<Mutator> Run)>
{
    ("keyfile", m => KeyFile.Parse(m.Mutate(desktopSeeds[m.Random.Next(desktopSeeds.Count)]))),
    ("desktop", m =>
    {
        string text = m.Mutate(desktopSeeds[m.Random.Next(desktopSeeds.Count)]);
        findings.Input = text;
        if (DesktopEntry.Parse("fuzz.desktop", "/fuzz/fuzz.desktop", text, m.Random.Next(2) == 0 ? "ja_JP.UTF-8" : null) is not { } entry)
            return;
        entry.IsVisible(["KDE", m.Mutate("niri")]);
        if (entry.Exec is null)
            return; // ExpandExec documents that it throws without an Exec key
        string[] files = [m.Mutate("/home/u/my file.txt"), m.Mutate("https://example.org/a?b=c&d"), m.Mutate("-rf")];
        entry.ExpandExec(files);
        foreach (var action in entry.Actions)
            entry.ExpandExec(action, files);
        if (entry.ExpandExec(files).Count > 0) // CommandLine documents that it throws for an empty Exec
            _ = AppLauncher.CommandLine(entry, null, files, "/usr/bin/foot");
    }),
    ("search", m =>
    {
        string query = m.Mutate("lib wri ffx " + (char)m.Random.Next(0x3000, 0x30ff));
        findings.Input = query;
        TextFold.Fold(query);
        AppSearch.Search(entries, query, id => m.Random.NextDouble() * 20, 50);
    }),
    ("mime", m =>
    {
        string dir = Path.Combine(work, "mime-" + m.Random.Next(4));
        Directory.CreateDirectory(Path.Combine(dir, "mime"));
        string g = m.Mutate(globs), sc = m.Mutate(subclasses), al = m.Mutate(aliases);
        findings.Input = $"globs2:\n{g}\n--- subclasses:\n{sc}\n--- aliases:\n{al}";
        File.WriteAllText(Path.Combine(dir, "mime/globs2"), g, Lenient);
        File.WriteAllText(Path.Combine(dir, "mime/subclasses"), sc, Lenient);
        File.WriteAllText(Path.Combine(dir, "mime/aliases"), al, Lenient);
        var db = MimeDatabase.Load([dir]);
        string name = m.Mutate("Report.Final.TAR.GZ");
        var type = db.FromFileName(name);
        db.WithParents(type).ToList();
        db.Guess(m.Mutate("file:///tmp/a%20b.pdf"));
        string list = Path.Combine(dir, "mimeapps.list");
        File.WriteAllText(list, m.Mutate(mimeapps), Lenient);
        DefaultApps.For(m.Mutate("text/plain"), entries, db, [list]);
    }),
    ("icontheme", m =>
    {
        string dir = Path.Combine(work, "icons-" + m.Random.Next(4));
        Directory.CreateDirectory(Path.Combine(dir, "t"));
        string index = m.Mutate(indexTheme);
        findings.Input = index;
        File.WriteAllText(Path.Combine(dir, "t/index.theme"), index, Lenient);
        new IconTheme("t", [dir]).Find(m.Mutate("firefox"), m.Random.Next(-8, 1024), m.Random.Next(-1, 4));
    }),
    ("usage", m =>
    {
        string path = Path.Combine(work, "usage.json");
        string json = m.Mutate(usage);
        findings.Input = json;
        File.WriteAllText(path, json, Lenient);
        var history = UsageHistory.Load(path);
        history.Boost(m.Mutate("a.desktop"));
    }),
    ("polkit-unescape", m =>
    {
        string line = m.Mutate("Password for \\303\\251l\\350ve:\\n\\t\\\\");
        findings.Input = line;
        Unescape(line);
    }),
};
targets.AddRange(Live.Targets(findings, work, tokens));

foreach (var (name, run) in targets.Where(t => only.Count == 0 || only.Contains(t.Name)))
{
    var mutator = new Mutator(Environment.TickCount ^ name.GetHashCode(), tokens);
    var clock = Stopwatch.StartNew();
    long iterations = 0, slowest = 0;
    Console.Write($"{name,-16}");
    try
    {
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            findings.Input = "";
            long start = Stopwatch.GetTimestamp();
            try
            {
                run(mutator);
            }
            catch (Exception e) when (e is not Live.FatalException)
            {
                findings.Add(name, e);
            }
            long ms = (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (ms > slowest)
                slowest = ms;
            if (ms > 1000)
                findings.Add(name, new TimeoutException($"one input took {ms} ms"));
            iterations++;
        }
    }
    catch (Live.FatalException e)
    {
        findings.Add(name, e.InnerException ?? e);
        Console.Write(" [server crashed]");
    }
    Console.WriteLine($" {iterations,9:N0} inputs  slowest {slowest,5} ms  findings {findings.Count(name)}");
}
Live.Shutdown();
Directory.Delete(work, recursive: true);
findings.Report();
return findings.Total > 0 ? 1 : 0;

static string Unescape(string s) => (string)typeof(oopshell.Services.Polkit.PolkitRequest)
    .GetMethod("Unescape", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, [s])!;

namespace oopshell.Fuzz
{
    static class Encodings
    {
        /// <summary>Writes lone surrogates as U+FFFD instead of throwing (mutations produce them).</summary>
        public static readonly Encoding Lenient = new UTF8Encoding(false, false);
    }

    static class Repo
    {
        public static readonly string Root = Find(AppContext.BaseDirectory);

        static string Find(string dir) => File.Exists(Path.Combine(dir, "oopshell.slnx")) ? dir : Find(Path.GetDirectoryName(dir)!);
    }

    /// <summary>Distinct failures (exception type + first oopshell frame), each saved once with its input.</summary>
    sealed class Findings(string dir)
    {
        readonly Dictionary<string, (string Target, Exception Error, int Hits)> seen = new();
        public string Input = "";

        public int Total => seen.Count;
        public int Count(string target) => seen.Values.Count(v => v.Target == target);

        public void Add(string target, Exception e)
        {
            if (e is System.Reflection.TargetInvocationException { InnerException: { } inner })
                e = inner;
            string frame = e.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains(" oopshell.") && !l.Contains("oopshell.Fuzz"))?.Trim() ?? "";
            string key = $"{target}|{e.GetType().Name}|{frame}";
            if (seen.TryGetValue(key, out var existing))
            {
                seen[key] = existing with { Hits = existing.Hits + 1 };
                return;
            }
            seen[key] = (target, e, 1);
            File.WriteAllText(Path.Combine(dir, $"{target}-{seen.Count}.txt"), $"{e}\n\n--- input ---\n{Input}", new UTF8Encoding(false, throwOnInvalidBytes: false));
        }

        public void Report()
        {
            Console.WriteLine();
            foreach (var (key, (target, error, hits)) in seen)
                Console.WriteLine($"[{target}] {error.GetType().Name}: {error.Message.Split('\n')[0]}  ({hits}×)\n    {key.Split('|')[2]}");
            Console.WriteLine(seen.Count == 0 ? "No findings." : $"{seen.Count} distinct findings; inputs in {dir}");
        }
    }
}
