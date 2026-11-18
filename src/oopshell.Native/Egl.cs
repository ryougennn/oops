using System.Runtime.InteropServices;

namespace oopshell.Native;

/// <summary>
/// P/Invoke surface of <c>libEGL.so.1</c> and <c>libwayland-egl.so.1</c>. Both are optional: they are only loaded
/// when the EGL path is used; check <see cref="IsAvailable"/> first.
/// </summary>
public static unsafe partial class Egl
{
    const string Lib = "libEGL.so.1";
    const string WaylandEglLib = "libwayland-egl.so.1";

    public const int EGL_FALSE = 0;
    public const int EGL_TRUE = 1;
    public const int EGL_NONE = 0x3038;
    public const int EGL_SUCCESS = 0x3000;
    public const int EGL_ALPHA_SIZE = 0x3021;
    public const int EGL_BLUE_SIZE = 0x3022;
    public const int EGL_GREEN_SIZE = 0x3023;
    public const int EGL_RED_SIZE = 0x3024;
    public const int EGL_DEPTH_SIZE = 0x3025;
    public const int EGL_STENCIL_SIZE = 0x3026;
    public const int EGL_SAMPLES = 0x3031;
    public const int EGL_SURFACE_TYPE = 0x3033;
    public const int EGL_WINDOW_BIT = 0x0004;
    public const int EGL_RENDERABLE_TYPE = 0x3040;
    public const int EGL_OPENGL_ES2_BIT = 0x0004;
    public const int EGL_OPENGL_ES3_BIT = 0x0040;
    public const int EGL_OPENGL_BIT = 0x0008;
    public const int EGL_EXTENSIONS = 0x3055;
    public const int EGL_VENDOR = 0x3053;
    public const int EGL_VERSION = 0x3054;
    public const int EGL_OPENGL_ES_API = 0x30A0;
    public const int EGL_OPENGL_API = 0x30A2;
    public const int EGL_CONTEXT_MAJOR_VERSION = 0x3098;
    public const int EGL_CONTEXT_MINOR_VERSION = 0x30FB;
    public const int EGL_PLATFORM_WAYLAND_KHR = 0x31D8;
    public const int EGL_BUFFER_AGE_EXT = 0x313D;

    [LibraryImport(Lib)] public static partial nint eglGetProcAddress(byte* name);
    [LibraryImport(Lib)] public static partial int eglGetError();
    [LibraryImport(Lib)] public static partial byte* eglQueryString(nint display, int name);
    [LibraryImport(Lib)] public static partial int eglInitialize(nint display, int* major, int* minor);
    [LibraryImport(Lib)] public static partial int eglTerminate(nint display);
    [LibraryImport(Lib)] public static partial int eglBindAPI(int api);
    [LibraryImport(Lib)] public static partial int eglChooseConfig(nint display, int* attribs, nint* configs, int size, int* count);
    [LibraryImport(Lib)] public static partial nint eglCreateContext(nint display, nint config, nint share, int* attribs);
    [LibraryImport(Lib)] public static partial int eglDestroyContext(nint display, nint context);
    [LibraryImport(Lib)] public static partial nint eglCreateWindowSurface(nint display, nint config, nint window, int* attribs);
    [LibraryImport(Lib)] public static partial int eglDestroySurface(nint display, nint surface);
    [LibraryImport(Lib)] public static partial int eglMakeCurrent(nint display, nint draw, nint read, nint context);
    [LibraryImport(Lib)] public static partial int eglSwapBuffers(nint display, nint surface);
    [LibraryImport(Lib)] public static partial int eglSwapInterval(nint display, int interval);
    [LibraryImport(Lib)] public static partial int eglQuerySurface(nint display, nint surface, int attribute, int* value);

    [LibraryImport(WaylandEglLib)] public static partial nint wl_egl_window_create(nint surface, int width, int height);
    [LibraryImport(WaylandEglLib)] public static partial void wl_egl_window_resize(nint window, int width, int height, int dx, int dy);
    [LibraryImport(WaylandEglLib)] public static partial void wl_egl_window_destroy(nint window);

    /// <summary>True if both libEGL and libwayland-egl can be loaded.</summary>
    public static bool IsAvailable => NativeLibrary.TryLoad(Lib, out _) && NativeLibrary.TryLoad(WaylandEglLib, out _);

    /// <summary><c>eglGetProcAddress</c> with a managed name; returns 0 if unknown.</summary>
    public static nint GetProcAddress(string name)
    {
        var utf8 = System.Text.Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* p = utf8)
            return eglGetProcAddress(p);
    }
}
