using System.Drawing;
using oopshell.Egl;

namespace oopshell.IntegrationTests;

/// <summary>GPU path. Runs on Mesa (llvmpipe/swrast works with a pixman sway); skips when EGL is unavailable.</summary>
[Collection("sway")]
[Trait("Requires", "GPU")]
public unsafe class EglTests(SwayFixture sway)
{
    const int GL_COLOR_BUFFER_BIT = 0x4000;

    [Fact]
    public void Gl_clear_reaches_the_screen()
    {
        using var app = sway.Connect();
        Assert.SkipUnless(oopshell.Native.Egl.IsAvailable, "libEGL/libwayland-egl not installed");
        using var window = new Toplevel(app);
        window.SetFullscreen(true);
        EglWindow egl;
        try
        {
            egl = EglWindow.Create(window);
        }
        catch (InvalidOperationException e)
        {
            Assert.Skip("EGL could not initialize here: " + e.Message);
            return;
        }
        using var _ = egl;
        var clearColor = (delegate* unmanaged[Cdecl]<float, float, float, float, void>)EglDisplay.GetProcAddress("glClearColor");
        var clear = (delegate* unmanaged[Cdecl]<int, void>)EglDisplay.GetProcAddress("glClear");
        var viewport = (delegate* unmanaged[Cdecl]<int, int, int, int, void>)EglDisplay.GetProcAddress("glViewport");
        int frames = 0;
        var ages = new List<int>();
        window.Render += frame =>
        {
            egl.MakeCurrent();
            egl.Resize(frame.BufferSize);
            ages.Add(egl.BufferAge);
            viewport(0, 0, frame.BufferSize.Width, frame.BufferSize.Height);
            clearColor(0.2f, 0.4f, 0.8f, 1f);
            clear(GL_COLOR_BUFFER_BIT);
            egl.SwapBuffers(frame.Damage);
            frames++;
            if (frames < 4)
                window.Invalidate();
        };
        app.RunUntil(() => frames >= 4 && window.LogicalSize == new Size(800, 600), what: "GL frames");
        app.RunFor(100);

        var (r, g, b) = sway.Screenshot()[400, 300];
        Assert.InRange(r, 0x32, 0x34);
        Assert.InRange(g, 0x65, 0x67);
        Assert.InRange(b, 0xCB, 0xCD);
        Assert.Equal(0, ages[0]);
    }
}
