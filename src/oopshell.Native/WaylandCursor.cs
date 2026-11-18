using System.Runtime.InteropServices;

namespace oopshell.Native;

/// <summary>P/Invoke surface of the optional <c>libwayland-cursor.so.0</c> (cursor theme fallback only).</summary>
public static unsafe partial class WaylandCursor
{
    public const string Lib = "libwayland-cursor.so.0";

    [StructLayout(LayoutKind.Sequential)]
    public struct Cursor
    {
        public uint ImageCount;
        public CursorImage** Images;
        public byte* Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CursorImage
    {
        public uint Width;
        public uint Height;
        public uint HotspotX;
        public uint HotspotY;
        public uint Delay;
    }

    public static bool IsAvailable => NativeLibrary.TryLoad(Lib, out _);

    [LibraryImport(Lib)] public static partial nint wl_cursor_theme_load(byte* name, int size, nint shm);
    [LibraryImport(Lib)] public static partial void wl_cursor_theme_destroy(nint theme);
    [LibraryImport(Lib)] public static partial Cursor* wl_cursor_theme_get_cursor(nint theme, byte* name);
    [LibraryImport(Lib)] public static partial nint wl_cursor_image_get_buffer(CursorImage* image);
}
