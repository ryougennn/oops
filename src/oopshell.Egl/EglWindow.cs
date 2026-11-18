using System.Drawing;
using System.Runtime.InteropServices;
using oopshell.Native;
using static oopshell.Native.Egl;

namespace oopshell.Egl;

/// <summary>Which GL flavour to create.</summary>
public enum GlApi
{
    GLES,
    OpenGL,
}

/// <summary>Context and framebuffer settings. All windows of an app share one context, so the first options win.</summary>
public sealed record EglOptions
{
    public static readonly EglOptions Default = new();

    public GlApi Api { get; init; } = GlApi.GLES;
    public int MajorVersion { get; init; } = 3;
    public int MinorVersion { get; init; } = 0;
    public int AlphaBits { get; init; } = 8;
    public int DepthBits { get; init; }
    /// <summary>8 by default, because Skia's GL backend needs a stencil buffer.</summary>
    public int StencilBits { get; init; } = 8;
    public int Samples { get; init; }
}

/// <summary>
/// One <c>EGLDisplay</c> (made from the app's <c>wl_display</c>), its config, and one context that every
/// <see cref="EglWindow"/> of the app uses. Created on first use.
/// </summary>
public sealed unsafe class EglDisplay : IDisposable
{
    static readonly Dictionary<OopsApp, EglDisplay> Displays = new();
    readonly HashSet<string> extensions;

    EglDisplay(OopsApp app, EglOptions options)
    {
        if (!IsAvailable)
            throw new NotSupportedException("libEGL.so.1 or libwayland-egl.so.1 is not installed.");
        App = app;
        Options = options;

        var getPlatformDisplay = (delegate* unmanaged[Cdecl]<int, nint, int*, nint>)GetProcAddress("eglGetPlatformDisplayEXT");
        if (getPlatformDisplay == null)
            throw new NotSupportedException("EGL lacks eglGetPlatformDisplayEXT (EGL_EXT_platform_base).");
        Handle = getPlatformDisplay(EGL_PLATFORM_WAYLAND_KHR, app.Display.Handle, null);
        int major, minor;
        if (Handle == 0 || eglInitialize(Handle, &major, &minor) == EGL_FALSE)
            throw Error("eglInitialize");
        extensions = (Marshal.PtrToStringUTF8((nint)eglQueryString(Handle, EGL_EXTENSIONS)) ?? "").Split(' ').ToHashSet();

        if (eglBindAPI(options.Api == GlApi.GLES ? EGL_OPENGL_ES_API : EGL_OPENGL_API) == EGL_FALSE)
            throw Error("eglBindAPI");
        int renderable = options.Api == GlApi.OpenGL ? EGL_OPENGL_BIT : options.MajorVersion >= 3 ? EGL_OPENGL_ES3_BIT : EGL_OPENGL_ES2_BIT;
        int* configAttribs = stackalloc int[]
        {
            EGL_SURFACE_TYPE, EGL_WINDOW_BIT,
            EGL_RENDERABLE_TYPE, renderable,
            EGL_RED_SIZE, 8, EGL_GREEN_SIZE, 8, EGL_BLUE_SIZE, 8,
            EGL_ALPHA_SIZE, options.AlphaBits,
            EGL_DEPTH_SIZE, options.DepthBits,
            EGL_STENCIL_SIZE, options.StencilBits,
            EGL_SAMPLES, options.Samples,
            EGL_NONE,
        };
        nint config;
        int count;
        if (eglChooseConfig(Handle, configAttribs, &config, 1, &count) == EGL_FALSE || count == 0)
            throw Error("eglChooseConfig");
        Config = config;

        int* contextAttribs = stackalloc int[]
        {
            EGL_CONTEXT_MAJOR_VERSION, options.MajorVersion,
            EGL_CONTEXT_MINOR_VERSION, options.MinorVersion,
            EGL_NONE,
        };
        Context = eglCreateContext(Handle, Config, 0, contextAttribs);
        if (Context == 0)
            throw Error("eglCreateContext");

        HasBufferAge = extensions.Contains("EGL_EXT_buffer_age");
        var swapWithDamage = GetProcAddress("eglSwapBuffersWithDamageKHR");
        if (swapWithDamage == 0)
            swapWithDamage = GetProcAddress("eglSwapBuffersWithDamageEXT");
        SwapBuffersWithDamage = (delegate* unmanaged[Cdecl]<nint, nint, int*, int, int>)swapWithDamage;
    }

    /// <summary>The app's EGL display, created with <paramref name="options"/> on first use.</summary>
    public static EglDisplay Get(OopsApp app, EglOptions? options = null)
    {
        if (!Displays.TryGetValue(app, out var display))
        {
            Displays[app] = display = new EglDisplay(app, options ?? EglOptions.Default);
            // Mesa caches EGLDisplays by wl_display address, which a later connection may reuse:
            // terminate before the connection goes away so no stale display survives.
            app.Disposing += display.Dispose;
        }
        return display;
    }

    public OopsApp App { get; }
    public EglOptions Options { get; }
    /// <summary><c>EGLDisplay</c>.</summary>
    public nint Handle { get; }
    /// <summary><c>EGLConfig</c>.</summary>
    public nint Config { get; }
    /// <summary>The shared <c>EGLContext</c>.</summary>
    public nint Context { get; }
    public bool HasBufferAge { get; }
    public bool HasSwapBuffersWithDamage => SwapBuffersWithDamage != null;
    internal delegate* unmanaged[Cdecl]<nint, nint, int*, int, int> SwapBuffersWithDamage { get; }

    public bool HasExtension(string name) => extensions.Contains(name);

    /// <summary>Raised before the display is terminated (e.g. so a GPU drawing library can drop its context).</summary>
    public event Action? Disposing;

    bool disposed;

    /// <summary>Looks up a GL or EGL function, for GL binding libraries (Silk.NET, OpenTK, Skia's GRGlInterface).</summary>
    public static nint GetProcAddress(string name) => Native.Egl.GetProcAddress(name);

    internal static Exception Error(string call) => new InvalidOperationException($"{call} failed (EGL error 0x{eglGetError():X4}).");

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Disposing?.Invoke();
        App.Disposing -= Dispose;
        Displays.Remove(App);
        eglMakeCurrent(Handle, 0, 0, 0);
        eglDestroyContext(Handle, Context);
        eglTerminate(Handle);
    }
}

/// <summary>
/// GPU rendering for a <see cref="Surface"/>: a <c>wl_egl_window</c> plus an <c>EGLSurface</c> on the app's shared
/// context. Swap interval is 0; pacing comes from oopshell's frame callbacks.
/// </summary>
public sealed unsafe class EglWindow : IDisposable
{
    readonly Surface surface;
    nint window;
    nint eglSurface;
    Size size;
    bool disposed;

    EglWindow(Surface surface, EglDisplay display)
    {
        this.surface = surface;
        Display = display;
        size = surface.BufferSize.IsEmpty ? new Size(1, 1) : surface.BufferSize;
        window = wl_egl_window_create(surface.NativeSurface, size.Width, size.Height);
        if (window == 0)
            throw new InvalidOperationException("wl_egl_window_create failed.");
        eglSurface = eglCreateWindowSurface(display.Handle, display.Config, window, null);
        if (eglSurface == 0)
            throw EglDisplay.Error("eglCreateWindowSurface");
        MakeCurrent();
        eglSwapInterval(display.Handle, 0);
    }

    /// <summary>Creates an EGL window for <paramref name="surface"/>. Throws <see cref="NotSupportedException"/> without EGL.</summary>
    public static EglWindow Create(Surface surface, EglOptions? options = null) => new(surface, EglDisplay.Get(surface.App, options));

    public EglDisplay Display { get; }

    /// <summary><c>EGLSurface</c>.</summary>
    public nint Handle => eglSurface;

    public Size Size => size;

    public void MakeCurrent()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (eglMakeCurrent(Display.Handle, eglSurface, eglSurface, Display.Context) == EGL_FALSE)
            throw EglDisplay.Error("eglMakeCurrent");
    }

    /// <summary>Resizes the underlying window (takes effect on the next swap). Cheap when unchanged.</summary>
    public void Resize(Size bufferSize)
    {
        if (bufferSize == size || bufferSize.IsEmpty)
            return;
        size = bufferSize;
        wl_egl_window_resize(window, size.Width, size.Height, 0, 0);
    }

    /// <summary>Age of the back buffer (EGL_EXT_buffer_age): 0 = undefined contents. Call after <see cref="MakeCurrent"/>.</summary>
    public int BufferAge
    {
        get
        {
            if (!Display.HasBufferAge)
                return 0;
            int age;
            return eglQuerySurface(Display.Handle, eglSurface, EGL_BUFFER_AGE_EXT, &age) == EGL_FALSE ? 0 : age;
        }
    }

    /// <summary>Presents the frame, reporting <paramref name="damage"/> (buffer pixels, top-left origin) when supported.</summary>
    public void SwapBuffers(Rectangle damage)
    {
        surface.PrepareCommit();
        int result;
        if (Display.HasSwapBuffersWithDamage && !damage.IsEmpty)
        {
            // EGL damage rectangles have a bottom-left origin.
            int* rect = stackalloc int[] { damage.X, size.Height - damage.Bottom, damage.Width, damage.Height };
            result = Display.SwapBuffersWithDamage(Display.Handle, eglSurface, rect, 1);
        }
        else
        {
            result = eglSwapBuffers(Display.Handle, eglSurface);
        }
        if (result == EGL_FALSE)
            throw EglDisplay.Error("eglSwapBuffers");
    }

    public void SwapBuffers() => SwapBuffers(new Rectangle(Point.Empty, size));

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        eglMakeCurrent(Display.Handle, 0, 0, 0);
        eglDestroySurface(Display.Handle, eglSurface);
        wl_egl_window_destroy(window);
        eglSurface = window = 0;
    }
}
