using System.Drawing;

namespace oopshell.IntegrationTests;

/// <summary>Deterministic CPU drawing for screenshot tests.</summary>
public static class Draw
{
    public static void Fill(ShmBuffer buffer, uint argb) => buffer.Pixels.Fill(argb);

    /// <summary>A gradient with a few sharp edges, so scaling or offset bugs show up in goldens.</summary>
    public static void TestPattern(ShmBuffer buffer)
    {
        int w = buffer.Size.Width, h = buffer.Size.Height;
        var pixels = buffer.Pixels;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                uint r = (uint)(x * 255 / Math.Max(w - 1, 1));
                uint g = (uint)(y * 255 / Math.Max(h - 1, 1));
                uint b = ((x / 50) + (y / 50)) % 2 == 0 ? 0x40u : 0xC0u;
                pixels[y * w + x] = 0xFF000000 | (r << 16) | (g << 8) | b;
            }
        }
    }

    /// <summary>Hooks a swapchain that fills each frame with <paramref name="argb"/>; returns a frame counter.</summary>
    public static Func<int> SolidColor(Surface surface, uint argb)
    {
        var chain = new ShmSwapchain(surface);
        int frames = 0;
        surface.Render += frame =>
        {
            using var buffer = chain.Acquire(frame);
            if (buffer is null)
                return;
            Fill(buffer, argb);
            buffer.Present(new Rectangle(Point.Empty, frame.BufferSize));
            frames++;
        };
        return () => frames;
    }
}
