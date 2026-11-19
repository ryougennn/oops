namespace oopshell.IntegrationTests;

/// <summary>A temporary directory removed on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("oopstest.").FullName;

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
