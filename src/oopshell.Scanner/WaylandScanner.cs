using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace oopshell.Scanner;

/// <summary>
/// Generates C# bindings for every Wayland protocol XML passed as an <c>AdditionalFiles</c> item
/// (the <c>WaylandProtocol</c> MSBuild item maps to those).
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class WaylandScanner : IIncrementalGenerator
{
    public static readonly DiagnosticDescriptor InvalidXml = new(
        "OOPS001", "Invalid protocol XML", "Protocol file '{0}' could not be read: {1}", "oopshell.Scanner", DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor UnknownReference = new(
        "OOPS002", "Unknown protocol reference", "'{0}' is referenced but not defined by any protocol in this project or its references; add its XML as a WaylandProtocol item", "oopshell.Scanner", DiagnosticSeverity.Error, true);

    const string AttributeName = "oopshell.Protocol.WaylandProtocolsAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var files = context.AdditionalTextsProvider
            .Where(text => text.Path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .Select((text, ct) => new ProtocolFile(text.Path, text.GetText(ct)?.ToString() ?? ""))
            .Collect();

        var ns = context.AnalyzerConfigOptionsProvider.Select((options, _) =>
            options.GlobalOptions.TryGetValue("build_property.WaylandProtocolNamespace", out var value) && value.Length > 0 ? value
            : options.GlobalOptions.TryGetValue("build_property.RootNamespace", out var root) && root.Length > 0 ? root
            : "Wayland.Protocols");

        // "interface-or-enum=namespace" pairs from referenced assemblies, as one comparable string so caching works.
        var external = context.CompilationProvider.Select((compilation, _) => string.Join("\n",
            compilation.SourceModule.ReferencedAssemblySymbols
                .SelectMany(a => a.GetAttributes())
                .Where(a => a.AttributeClass?.ToDisplayString() == AttributeName && a.ConstructorArguments.Length == 2)
                .SelectMany(a => ((string?)a.ConstructorArguments[1].Value ?? "")
                    .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(name => name + "=" + (string?)a.ConstructorArguments[0].Value))
                .OrderBy(s => s, StringComparer.Ordinal)));

        context.RegisterSourceOutput(files.Combine(ns).Combine(external), (spc, input) =>
        {
            var ((protocolFiles, localNamespace), externalText) = input;
            var result = Generate(protocolFiles, localNamespace, ParseExternal(externalText));
            foreach (var diagnostic in result.Diagnostics)
                spc.ReportDiagnostic(diagnostic);
            foreach (var (hint, source) in result.Sources)
                spc.AddSource(hint, SourceText.From(source, System.Text.Encoding.UTF8));
        });
    }

    /// <summary>The whole generator as a pure function, so tests can call it directly.</summary>
    public static GenerationResult Generate(ImmutableArray<ProtocolFile> files, string localNamespace, IReadOnlyDictionary<string, string> external)
    {
        var result = new GenerationResult();
        var protocols = new List<Protocol>();
        foreach (var file in files.OrderBy(f => Path.GetFileName(f.Path), StringComparer.Ordinal))
        {
            try
            {
                protocols.Add(ProtocolParser.Parse(file.Text));
            }
            catch (ProtocolFormatException e)
            {
                result.Diagnostics.Add(Diagnostic.Create(InvalidXml, Location.None, Path.GetFileName(file.Path), e.Message));
            }
        }
        if (protocols.Count == 0)
            return result;

        var resolver = new TypeResolver(localNamespace, protocols, external);
        var emitter = new Emitter(resolver);
        foreach (var protocol in protocols)
            result.Sources.Add((protocol.Name + ".g.cs", emitter.EmitProtocol(protocol)));
        result.Sources.Add(("WaylandInterfaces.g.cs", emitter.EmitTables(protocols)));

        foreach (var missing in resolver.Missing)
            result.Diagnostics.Add(Diagnostic.Create(UnknownReference, Location.None, missing));
        return result;
    }

    static Dictionary<string, string> ParseExternal(string text)
    {
        var map = new Dictionary<string, string>();
        foreach (var line in text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = line.IndexOf('=');
            map[line.Substring(0, eq)] = line.Substring(eq + 1);
        }
        return map;
    }
}

public sealed class ProtocolFile(string path, string text) : IEquatable<ProtocolFile>
{
    public string Path { get; } = path;
    public string Text { get; } = text;

    public bool Equals(ProtocolFile? other) => other is not null && other.Path == Path && other.Text == Text;
    public override bool Equals(object? obj) => Equals(obj as ProtocolFile);
    public override int GetHashCode() => Path.GetHashCode() ^ Text.Length;
}

public sealed class GenerationResult
{
    public List<(string HintName, string Source)> Sources { get; } = new();
    public List<Diagnostic> Diagnostics { get; } = new();
}
