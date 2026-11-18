using System.Runtime.InteropServices;

namespace oopshell.Services.Desktop;

/// <summary>
/// Renders SVG icons (most icon themes ship only SVG) with the system's librsvg and cairo, loaded at run
/// time: no managed dependency, and nothing breaks where they are missing (<see cref="IsAvailable"/>).
/// <code>
/// if (theme.Find("firefox", 48) is { } path &amp;&amp; path.EndsWith(".svg") &amp;&amp; SvgRenderer.Render(path, 96, 96) is { } bgra)
///     image = SKImage.FromPixelCopy(new SKImageInfo(96, 96, SKColorType.Bgra8888, SKAlphaType.Premul), bgra);
/// </code>
/// </summary>
public static unsafe partial class SvgRenderer
{
    const string Rsvg = "librsvg-2.so.2", Cairo = "libcairo.so.2", GObject = "libgobject-2.0.so.0", GLib = "libglib-2.0.so.0";
    const int CAIRO_FORMAT_ARGB32 = 0;

    [StructLayout(LayoutKind.Sequential)]
    struct RsvgRectangle
    {
        public double X, Y, Width, Height;
    }

    [LibraryImport(Rsvg, StringMarshalling = StringMarshalling.Utf8)] private static partial nint rsvg_handle_new_from_file(string path, nint* error);
    [LibraryImport(Rsvg)] private static partial int rsvg_handle_render_document(nint handle, nint cairo, RsvgRectangle* viewport, nint* error);
    [LibraryImport(Cairo)] private static partial nint cairo_image_surface_create(int format, int width, int height);
    [LibraryImport(Cairo)] private static partial nint cairo_create(nint surface);
    [LibraryImport(Cairo)] private static partial void cairo_destroy(nint cairo);
    [LibraryImport(Cairo)] private static partial void cairo_surface_flush(nint surface);
    [LibraryImport(Cairo)] private static partial byte* cairo_image_surface_get_data(nint surface);
    [LibraryImport(Cairo)] private static partial int cairo_image_surface_get_stride(nint surface);
    [LibraryImport(Cairo)] private static partial void cairo_surface_destroy(nint surface);
    [LibraryImport(GObject)] private static partial void g_object_unref(nint obj);
    [LibraryImport(GLib)] private static partial void g_error_free(nint error);

    static readonly Lazy<bool> Available = new(() =>
        NativeLibrary.TryLoad(Rsvg, out _) && NativeLibrary.TryLoad(Cairo, out _) && NativeLibrary.TryLoad(GObject, out _) && NativeLibrary.TryLoad(GLib, out _));

    /// <summary>True if librsvg and cairo could be loaded.</summary>
    public static bool IsAvailable => Available.Value;

    /// <summary>
    /// Renders the SVG file scaled to fit <paramref name="width"/>×<paramref name="height"/> (aspect kept,
    /// centered). Returns premultiplied BGRA pixels, <c>width * 4</c> bytes per row, or null if the file cannot be
    /// rendered or librsvg is missing.
    /// </summary>
    public static byte[]? Render(string path, int width, int height)
    {
        if (width <= 0 || height <= 0 || width > 4096 || height > 4096 || !IsAvailable || !File.Exists(path))
            return null;
        nint error = 0;
        nint handle = rsvg_handle_new_from_file(path, &error);
        if (handle == 0)
        {
            if (error != 0)
                g_error_free(error);
            return null;
        }
        nint surface = cairo_image_surface_create(CAIRO_FORMAT_ARGB32, width, height);
        nint cairo = cairo_create(surface);
        try
        {
            var viewport = new RsvgRectangle { Width = width, Height = height };
            if (rsvg_handle_render_document(handle, cairo, &viewport, &error) == 0)
            {
                if (error != 0)
                    g_error_free(error);
                return null;
            }
            cairo_surface_flush(surface);
            // Cairo's ARGB32 is premultiplied and native-endian: B, G, R, A in memory on little-endian machines.
            byte* data = cairo_image_surface_get_data(surface);
            int stride = cairo_image_surface_get_stride(surface);
            var pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
                new ReadOnlySpan<byte>(data + y * stride, width * 4).CopyTo(pixels.AsSpan(y * width * 4));
            return pixels;
        }
        finally
        {
            cairo_destroy(cairo);
            cairo_surface_destroy(surface);
            g_object_unref(handle);
        }
    }
}
