using System;
using Nexus.Service.Peripherals.Nzxt;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Round-trips the encoder through an independent decoder written straight from the format
/// description, so a wrong opcode cannot pass by agreeing with itself.
/// </summary>
public class Q565EncoderTests
{
    private static byte[] EncodeFrame(byte[] rgba, int w, int h, int turns = 0)
    {
        var dest = new byte[Q565Encoder.MaxEncodedLength(w, h)];
        var n = Q565Encoder.Encode(rgba, w, h, turns, dest);
        return dest.AsSpan(0, n).ToArray();
    }

    private static ushort[] Decode(byte[] stream, out int width, out int height)
    {
        Assert.Equal((byte)'q', stream[0]);
        Assert.Equal((byte)'5', stream[1]);
        Assert.Equal((byte)'6', stream[2]);
        Assert.Equal((byte)'5', stream[3]);
        width = stream[4] | (stream[5] << 8);
        height = stream[6] | (stream[7] << 8);

        var pixels = new ushort[width * height];
        var palette = new ushort[64];
        ushort prev = 0;
        int r = 0, g = 0, b = 0;
        int i = 8, o = 0;
        while (o < pixels.Length)
        {
            byte op = stream[i++];
            if (op == 0xFF) break;
            if (op == 0xFE)
            {
                prev = (ushort)(stream[i] | (stream[i + 1] << 8));
                i += 2;
                Split(prev, out r, out g, out b);
                palette[Hash(prev)] = prev;
            }
            else if ((op & 0xC0) == 0xC0)
            {
                int run = (op & 0x3F) + 1;
                for (int k = 0; k < run && o < pixels.Length; k++) pixels[o++] = prev;
                continue;
            }
            else if ((op & 0xC0) == 0x00)
            {
                prev = palette[op & 0x3F];
                Split(prev, out r, out g, out b);
            }
            else if ((op & 0xC0) == 0x40)
            {
                r = Wrap(r + (((op >> 4) & 0x03) - 2), 5);
                g = Wrap(g + (((op >> 2) & 0x03) - 2), 6);
                b = Wrap(b + ((op & 0x03) - 2), 5);
                prev = Join(r, g, b);
                // A one-byte pixel does not enter the palette.
            }
            else if ((op & 0xE0) == 0x80)
            {
                int dg = (op & 0x1F) - 16;
                byte second = stream[i++];
                int drg = ((second >> 4) & 0x0F) - 8;
                int dbg = (second & 0x0F) - 8;
                g = Wrap(g + dg, 6);
                r = Wrap(r + dg + drg, 5);
                b = Wrap(b + dg + dbg, 5);
                prev = Join(r, g, b);
                palette[Hash(prev)] = prev;
            }
            else
            {
                throw new InvalidOperationException($"unexpected opcode 0x{op:X2}");
            }
            pixels[o++] = prev;
        }
        Assert.Equal(pixels.Length, o);
        return pixels;
    }

    private static int Hash(ushort p) => ((p & 0xFF) + ((p >> 8) & 0xFF)) & 0x3F;
    private static void Split(ushort p, out int r, out int g, out int b)
    { r = (p >> 11) & 0x1F; g = (p >> 5) & 0x3F; b = p & 0x1F; }
    private static ushort Join(int r, int g, int b) => (ushort)((r << 11) | (g << 5) | b);
    private static int Wrap(int v, int bits) => v & ((1 << bits) - 1);

    private static ushort Expect(byte r, byte g, byte b) =>
        (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));

    [Fact]
    public void Round_trips_a_solid_frame_and_stays_tiny()
    {
        const int W = 64, H = 64;
        var rgba = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++)
        {
            rgba[i * 4] = 0xFF; rgba[(i * 4) + 1] = 0x00; rgba[(i * 4) + 2] = 0x00; rgba[(i * 4) + 3] = 0xFF;
        }

        var stream = EncodeFrame(rgba, W, H);
        var pixels = Decode(stream, out var w, out var h);

        Assert.Equal(W, w);
        Assert.Equal(H, h);
        Assert.All(pixels, p => Assert.Equal(Expect(0xFF, 0, 0), p));
        // Runs collapse a flat frame to a handful of bytes.
        Assert.True(stream.Length < 128, $"solid frame took {stream.Length} bytes");
    }

    [Fact]
    public void Round_trips_a_gradient_exactly()
    {
        const int W = 61, H = 37; // not a multiple of the run length, and not square
        var rgba = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int i = ((y * W) + x) * 4;
                rgba[i] = (byte)(x * 4);
                rgba[i + 1] = (byte)(y * 7);
                rgba[i + 2] = (byte)((x * y) % 256);
                rgba[i + 3] = 0xFF;
            }
        }

        var pixels = Decode(EncodeFrame(rgba, W, H), out _, out _);

        for (int i = 0; i < W * H; i++)
        {
            var src = i * 4;
            Assert.Equal(Expect(rgba[src], rgba[src + 1], rgba[src + 2]), pixels[i]);
        }
    }

    [Fact]
    public void Rotation_is_applied_during_the_encode()
    {
        const int W = 8, H = 8;
        var rgba = new byte[W * H * 4];
        // One white pixel at the top-left corner.
        rgba[0] = 0xFF; rgba[1] = 0xFF; rgba[2] = 0xFF; rgba[3] = 0xFF;

        var pixels = Decode(EncodeFrame(rgba, W, H, turns: 1), out _, out _);

        // dest(x,y) reads src(y, h-1-x), so the source's top-left lands top-right.
        var white = Expect(0xFF, 0xFF, 0xFF);
        Assert.Equal(white, pixels[W - 1]);
        Assert.Equal(1, System.Linq.Enumerable.Count(pixels, p => p == white));
    }

    [Fact]
    public void Frames_are_orders_of_magnitude_smaller_than_raw_rgba()
    {
        const int W = 320, H = 320;
        var rgba = new byte[W * H * 4];
        var rng = new Random(7);
        // Blocky content, which is what a widget panel actually looks like.
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int i = ((y * W) + x) * 4;
                byte v = (byte)(((x / 40) + (y / 40)) % 2 == 0 ? 20 : 200);
                rgba[i] = v; rgba[i + 1] = (byte)(v / 2); rgba[i + 2] = (byte)(255 - v);
                rgba[i + 3] = 0xFF;
            }
        }
        _ = rng;

        var stream = EncodeFrame(rgba, W, H);

        Assert.True(stream.Length * 20 < rgba.Length, $"{stream.Length} vs {rgba.Length} raw");
    }
}
