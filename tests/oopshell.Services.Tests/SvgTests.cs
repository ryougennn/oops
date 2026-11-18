using oopshell.Services.Desktop;

namespace oopshell.Services.Tests;

public sealed class SvgTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("oopssvg.").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    static (byte B, byte G, byte R, byte A) Pixel(byte[] bgra, int width, int x, int y)
    {
        int i = (y * width + x) * 4;
        return (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]);
    }

    [Fact]
    public void Renders_an_svg_fitted_and_centered_as_premultiplied_bgra()
    {
        Assert.SkipUnless(SvgRenderer.IsAvailable, "librsvg/cairo not installed");
        string path = Path.Combine(dir, "wide.svg");
        // Twice as wide as tall: red left half, blue right half.
        File.WriteAllText(path, """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 20 10" width="20" height="10">
              <rect x="0" y="0" width="10" height="10" fill="#ff0000"/>
              <rect x="10" y="0" width="10" height="10" fill="#0000ff"/>
            </svg>
            """);
        var bgra = SvgRenderer.Render(path, 20, 20);
        Assert.NotNull(bgra);
        Assert.Equal(20 * 20 * 4, bgra.Length);
        Assert.Equal((0, 0, 255, 255), Pixel(bgra, 20, 2, 10));   // red
        Assert.Equal((255, 0, 0, 255), Pixel(bgra, 20, 17, 10));  // blue
        Assert.Equal(0, Pixel(bgra, 20, 10, 1).A);                // letterboxed: transparent above
        Assert.Equal(0, Pixel(bgra, 20, 10, 18).A);
    }

    [Fact]
    public void Broken_or_missing_files_give_null()
    {
        Assert.SkipUnless(SvgRenderer.IsAvailable, "librsvg/cairo not installed");
        string broken = Path.Combine(dir, "broken.svg");
        File.WriteAllText(broken, "<svg this is not xml");
        Assert.Null(SvgRenderer.Render(broken, 16, 16));
        Assert.Null(SvgRenderer.Render(Path.Combine(dir, "missing.svg"), 16, 16));
        Assert.Null(SvgRenderer.Render(broken, 0, 16));
    }
}
