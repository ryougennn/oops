using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using oopshell.Scanner;

namespace oopshell.Scanner.Tests;

public class ScannerTests
{
    static readonly string RepoRoot = FindRepoRoot();

    static readonly string[] GoldenProtocols =
    [
        "protocols/wayland/wayland.xml",
        "protocols/wayland-protocols/xdg-shell.xml",
        "protocols/wlr-protocols/wlr-layer-shell-unstable-v1.xml",
    ];

    /// <summary>
    /// Compares generator output with tests/oopshell.Scanner.Tests/Goldens. To accept intentional changes, run
    /// <c>UPDATE_GOLDENS=1 dotnet test tests/oopshell.Scanner.Tests</c> and review the diff.
    /// </summary>
    [Fact]
    public void Output_matches_golden_files()
    {
        var files = GoldenProtocols.Select(p => new ProtocolFile(p, File.ReadAllText(Path.Combine(RepoRoot, p)))).ToImmutableArray();
        var result = WaylandScanner.Generate(files, "oopshell.Protocol", new Dictionary<string, string>());

        Assert.Empty(result.Diagnostics);
        string goldenDir = Path.Combine(RepoRoot, "tests/oopshell.Scanner.Tests/Goldens");
        bool update = Environment.GetEnvironmentVariable("UPDATE_GOLDENS") == "1";
        foreach (var (hint, source) in result.Sources)
        {
            string path = Path.Combine(goldenDir, hint);
            if (update)
                File.WriteAllText(path, source);
            Assert.True(File.Exists(path), $"Missing golden {hint}; run with UPDATE_GOLDENS=1.");
            Assert.Equal(File.ReadAllText(path), source);
        }
    }

    [Fact]
    public void Output_is_deterministic()
    {
        var files = GoldenProtocols.Select(p => new ProtocolFile(p, File.ReadAllText(Path.Combine(RepoRoot, p)))).ToImmutableArray();
        var first = WaylandScanner.Generate(files, "X", new Dictionary<string, string>());
        var second = WaylandScanner.Generate(files.Reverse().ToImmutableArray(), "X", new Dictionary<string, string>());
        Assert.Equal(first.Sources, second.Sources);
    }

    [Fact]
    public void Bad_xml_reports_OOPS001()
    {
        var result = WaylandScanner.Generate([new ProtocolFile("broken.xml", "<protocol name='x'><interface>")], "X", new Dictionary<string, string>());
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("OOPS001", diagnostic.Id);
        Assert.Contains("broken.xml", diagnostic.GetMessage());
    }

    [Fact]
    public void Missing_attribute_reports_OOPS001()
    {
        var result = WaylandScanner.Generate([new ProtocolFile("a.xml", "<protocol name='x'><interface version='1'/></protocol>")], "X", new Dictionary<string, string>());
        Assert.Equal("OOPS001", Assert.Single(result.Diagnostics).Id);
    }

    [Fact]
    public void Unknown_interface_reference_reports_OOPS002()
    {
        const string xml = """
            <protocol name="test">
              <interface name="test_thing" version="1">
                <request name="attach"><arg name="target" type="object" interface="no_such_interface"/></request>
              </interface>
            </protocol>
            """;
        var result = WaylandScanner.Generate([new ProtocolFile("t.xml", xml)], "X", new Dictionary<string, string>());
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("OOPS002", diagnostic.Id);
        Assert.Contains("no_such_interface", diagnostic.GetMessage());
    }

    /// <summary>A user protocol in another assembly that references core interfaces compiles cleanly.</summary>
    [Fact]
    public void User_protocol_referencing_core_interfaces_compiles()
    {
        const string xml = """
            <protocol name="test_effects">
              <interface name="test_effects_manager" version="2">
                <request name="get_blur">
                  <arg name="id" type="new_id" interface="test_blur"/>
                  <arg name="surface" type="object" interface="wl_surface"/>
                  <arg name="transform" type="uint" enum="wl_output.transform"/>
                </request>
                <request name="destroy" type="destructor"/>
              </interface>
              <interface name="test_blur" version="2">
                <request name="set_strength" since="2"><arg name="strength" type="fixed"/></request>
                <request name="set_name"><arg name="name" type="string" allow-null="true"/></request>
                <request name="set_data"><arg name="data" type="array"/><arg name="fd" type="fd"/></request>
                <event name="applied"><arg name="output" type="object" interface="wl_output" allow-null="true"/><arg name="keys" type="array"/></event>
                <event name="done"/>
                <enum name="flags" bitfield="true"><entry name="a" value="1"/><entry name="b" value="0x2"/></enum>
              </interface>
            </protocol>
            """;
        var protocolAssembly = typeof(oopshell.Protocol.WlSurface).Assembly;
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && a.Location.Length > 0 && !a.GetName().Name!.StartsWith("oopshell"))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Append(MetadataReference.CreateFromFile(protocolAssembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(oopshell.Native.WlArgument).Assembly.Location));
        var compilation = CSharpCompilation.Create("UserProtocols",
            [CSharpSyntaxTree.ParseText("namespace User; public static class Anchor { }", cancellationToken: TestContext.Current.CancellationToken)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));

        var driver = CSharpGeneratorDriver.Create([new WaylandScanner().AsSourceGenerator()],
            additionalTexts: [new InMemoryText("effects.xml", xml)],
            optionsProvider: new TestOptions("User.Protocols"));
        var ct = TestContext.Current.CancellationToken;
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics, ct);

        Assert.Empty(generatorDiagnostics);
        var errors = output.GetDiagnostics(ct).Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
        var generated = string.Join("\n", output.SyntaxTrees.Select(t => t.GetText(ct).ToString()));
        Assert.Contains("global::oopshell.Protocol.WlSurface.NativeInterface", generated);
        Assert.Contains("global::oopshell.Protocol.WlOutputTransform transform", generated);
    }

    static string FindRepoRoot([CallerFilePath] string path = "")
    {
        var dir = Path.GetDirectoryName(path);
        while (dir is not null && !File.Exists(Path.Combine(dir, "SPEC.md")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Repository root not found.");
    }

    sealed class InMemoryText(string path, string text) : AdditionalText
    {
        public override string Path => path;
        public override Microsoft.CodeAnalysis.Text.SourceText GetText(CancellationToken ct = default) => Microsoft.CodeAnalysis.Text.SourceText.From(text);
    }

    sealed class TestOptions(string ns) : Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptionsProvider
    {
        public override Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptions GlobalOptions { get; } = new Options(ns);
        public override Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptions GetOptions(SyntaxTree tree) => new Options(null);
        public override Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptions GetOptions(AdditionalText textFile) => new Options(null);

        sealed class Options(string? ns) : Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptions
        {
            public override bool TryGetValue(string key, out string value)
            {
                value = ns ?? "";
                return ns is not null && key == "build_property.WaylandProtocolNamespace";
            }
        }
    }
}
