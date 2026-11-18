using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace oopshell.IntegrationTests;

/// <summary>Minimal 8-bit RGB/RGBA PNG codec (what grim writes), so screenshot tests need no image library.</summary>
public sealed class Png(int width, int height, byte[] rgba)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    /// <summary>RGBA bytes, row-major.</summary>
    public byte[] Rgba { get; } = rgba;

    public (byte R, byte G, byte B) this[int x, int y]
    {
        get
        {
            int i = (y * Width + x) * 4;
            return (Rgba[i], Rgba[i + 1], Rgba[i + 2]);
        }
    }

    public static Png Decode(byte[] file)
    {
        int width = 0, height = 0, colorType = 0;
        var idat = new MemoryStream();
        for (int pos = 8; pos < file.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(pos));
            string type = Encoding.ASCII.GetString(file, pos + 4, 4);
            var data = file.AsSpan(pos + 8, length);
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                if (data[8] != 8 || data[12] != 0)
                    throw new NotSupportedException("Only 8-bit non-interlaced PNGs are supported.");
                colorType = data[9];
            }
            else if (type == "IDAT")
            {
                idat.Write(data);
            }
            pos += 12 + length;
        }
        int channels = colorType switch { 2 => 3, 6 => 4, _ => throw new NotSupportedException($"PNG color type {colorType}.") };

        idat.Position = 0;
        using var inflated = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
            z.CopyTo(inflated);
        var raw = inflated.ToArray();

        int stride = width * channels;
        var previous = new byte[stride];
        var current = new byte[stride];
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            int filter = raw[y * (stride + 1)];
            Array.Copy(raw, y * (stride + 1) + 1, current, 0, stride);
            Unfilter(filter, current, previous, channels);
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                rgba[o] = current[x * channels];
                rgba[o + 1] = current[x * channels + 1];
                rgba[o + 2] = current[x * channels + 2];
                rgba[o + 3] = channels == 4 ? current[x * channels + 3] : (byte)255;
            }
            (previous, current) = (current, previous);
        }
        return new Png(width, height, rgba);
    }

    static void Unfilter(int filter, byte[] line, byte[] previous, int bpp)
    {
        for (int i = 0; i < line.Length; i++)
        {
            int left = i >= bpp ? line[i - bpp] : 0;
            int up = previous[i];
            int upLeft = i >= bpp ? previous[i - bpp] : 0;
            line[i] = (byte)(line[i] + filter switch
            {
                0 => 0,
                1 => left,
                2 => up,
                3 => (left + up) / 2,
                4 => Paeth(left, up, upLeft),
                _ => throw new NotSupportedException($"PNG filter {filter}."),
            });
        }
    }

    static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    public byte[] Encode()
    {
        var raw = new MemoryStream();
        for (int y = 0; y < Height; y++)
        {
            raw.WriteByte(0);
            raw.Write(Rgba, y * Width * 4, Width * 4);
        }
        var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(raw.ToArray());

        var output = new MemoryStream();
        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), Height);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(output, "IHDR", header);
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    static void WriteChunk(Stream output, string type, byte[] data)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        output.Write(buffer);
        var typeAndData = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        output.Write(typeAndData);
        BinaryPrimitives.WriteUInt32BigEndian(buffer, Crc32(typeAndData));
        output.Write(buffer);
    }

    static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }

    /// <summary>
    /// Compares with <c>tests/goldens/{name}.png</c>, allowing <paramref name="tolerance"/> per channel.
    /// Set UPDATE_GOLDENS=1 (or run tests/update-goldens.sh) to rewrite the golden instead.
    /// </summary>
    public void AssertMatchesGolden(string name, int tolerance = 2)
    {
        string path = Path.Combine(SwayFixture.RepoRoot, "tests/goldens", name + ".png");
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDENS") == "1")
            File.WriteAllBytes(path, Encode());
        Assert.True(File.Exists(path), $"Missing golden {path}; run tests/update-goldens.sh.");
        var golden = Decode(File.ReadAllBytes(path));
        Assert.Equal((golden.Width, golden.Height), (Width, Height));
        for (int i = 0; i < Rgba.Length; i++)
        {
            if (Math.Abs(Rgba[i] - golden.Rgba[i]) > tolerance)
            {
                int pixel = i / 4;
                Assert.Fail($"Pixel ({pixel % Width},{pixel / Width}) channel {i % 4}: {Rgba[i]} vs golden {golden.Rgba[i]}.");
            }
        }
    }
}
