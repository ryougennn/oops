using System.Drawing;

namespace oopshell.IntegrationTests;

[Collection("sway")]
public class SwapchainTests(SwayFixture sway)
{
    [Fact]
    public void Buffers_are_reused_and_ages_reflect_history()
    {
        using var app = sway.Connect();
        using var window = new Toplevel(app);
        using var chain = new ShmSwapchain(window);
        var ages = new List<int>();
        var damages = new List<Rectangle>();
        window.Render += frame =>
        {
            using var buffer = chain.Acquire(frame);
            if (buffer is null)
                return;
            ages.Add(buffer.Age);
            damages.Add(buffer.Damage);
            buffer.Present(frame.Damage);
            if (ages.Count < 12)
                window.Invalidate(new Rectangle(ages.Count * 10, 0, 10, 10));
        };
        app.RunUntil(() => ages.Count >= 12 && window.LogicalSize.Width == 800, what: "12 frames");

        // A new buffer has undefined contents; once buffers cycle, ages are small positive numbers.
        Assert.Equal(0, ages[0]);
        Assert.Contains(ages.Skip(4), age => age is 1 or 2 or 3);
        // With a known age, the repair area is the recent small damages, not the whole buffer.
        int last = ages.Count - 1;
        if (ages[last] > 0)
            Assert.True(damages[last].Width * damages[last].Height < 800 * 600 / 4, $"repair damage {damages[last]}");
    }

    [Fact]
    public void Two_buffer_chain_under_constant_invalidation_never_stalls()
    {
        using var app = sway.Connect();
        using var window = new Toplevel(app);
        using var chain = new ShmSwapchain(window, maxBuffers: 2);
        int presented = 0, skipped = 0;
        window.Render += frame =>
        {
            using var buffer = chain.Acquire(frame);
            if (buffer is null)
            {
                skipped++;
                return;
            }
            buffer.Present();
            presented++;
            if (presented < 30)
                window.Invalidate();
        };
        // Skipped frames (no free buffer) must be retried when a buffer is released, never dropped for good.
        app.RunUntil(() => presented >= 30, what: "30 frames with two buffers");
    }

    [Fact]
    public void Single_buffer_chain_is_rejected()
    {
        using var app = sway.Connect();
        using var window = new Toplevel(app);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ShmSwapchain(window, maxBuffers: 1));
    }

    [Fact]
    public void Resize_reallocates_with_age_zero()
    {
        using var app = sway.Connect();
        using var first = new Toplevel(app);
        using var chain = new ShmSwapchain(first);
        var log = new List<(Size Size, int Age)>();
        first.Render += frame =>
        {
            using var buffer = chain.Acquire(frame)!;
            log.Add((buffer.Size, buffer.Age));
            buffer.Present();
        };
        app.RunUntil(() => log.Any(l => l.Size.Width == 800), what: "full width");

        using var second = new Toplevel(app);
        Draw.SolidColor(second, 0xFF000000);
        app.RunUntil(() => log.Any(l => l.Size.Width == 400), what: "half width");

        var resized = log.First(l => l.Size.Width == 400);
        Assert.Equal(0, resized.Age);
    }

    [Fact]
    public void Presented_reports_frame_timing()
    {
        using var app = sway.Connect();
        using var window = new Toplevel(app);
        Draw.SolidColor(window, 0xFF000000);
        var infos = new List<PresentationInfo>();
        window.Presented += infos.Add;
        window.Invalidate();
        app.RunUntil(() => infos.Count >= 1, what: "presentation feedback");
        Assert.True(infos[0].Discarded || infos[0].Timestamp > TimeSpan.Zero);
    }
}
