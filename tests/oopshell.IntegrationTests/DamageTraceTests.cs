using System.Drawing;
using System.Runtime.InteropServices;

namespace oopshell.IntegrationTests;

/// <summary>Checks the actual requests on the wire with WAYLAND_DEBUG=1 (libwayland logs them to stderr).</summary>
[Collection("sway")]
public partial class DamageTraceTests(SwayFixture sway)
{
    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int setenv(string name, string value, int overwrite);
    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int unsetenv(string name);
    [LibraryImport("libc")]
    private static partial int dup(int fd);
    [LibraryImport("libc")]
    private static partial int dup2(int fd, int fd2);

    [Fact]
    public void Partial_invalidation_sends_partial_damage_buffer()
    {
        sway.RequireSway();
        string log = Path.Combine(sway.RuntimeDir, "wayland-debug.log");
        int savedStderr = dup(2);
        using (var file = File.Create(log))
        {
            // libwayland reads WAYLAND_DEBUG from the C environment at connect time and logs to fd 2.
            setenv("WAYLAND_DEBUG", "client", 1);
            dup2((int)file.SafeFileHandle.DangerousGetHandle(), 2);
            try
            {
                using var app = sway.Connect();
                using var window = new Toplevel(app);
                var frames = Draw.SolidColor(window, 0xFF000000);
                app.RunUntil(() => frames() >= 1 && window.LogicalSize.Width == 800, what: "first frame");
                int before = frames();
                window.Invalidate(new Rectangle(10, 20, 30, 40));
                app.RunUntil(() => frames() > before, what: "partial frame");

                // The swapchain was asked to present the whole buffer by Draw.SolidColor; present the frame
                // damage instead to see it on the wire.
                var chain = new ShmSwapchain(window);
                window.Render += frame =>
                {
                    using var buffer = chain.Acquire(frame);
                    buffer?.Present(frame.Damage);
                };
                window.Invalidate(new Rectangle(10, 20, 30, 40));
                app.RunFor(200);
                chain.Dispose();
            }
            finally
            {
                dup2(savedStderr, 2);
                unsetenv("WAYLAND_DEBUG");
            }
        }
        string trace = File.ReadAllText(log);
        Assert.Contains("wl_surface#", trace);
        Assert.Contains(".damage_buffer(10, 20, 30, 40)", trace);
    }
}
