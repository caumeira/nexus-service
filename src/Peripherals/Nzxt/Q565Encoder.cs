using System;

namespace Nexus.Service.Peripherals.Nzxt;

/// <summary>
/// Q565 encoder - QOI adapted to 16-bit RGB565, which is the only compressed format this
/// cooler's LCD accepts (bulk format byte <see cref="KrakenProtocol.BulkFormatQ565"/>).
/// Format per github.com/seritools/q565; bench-verified against the panel, which decodes
/// what this produces.
///
/// Why it matters: a 640x640 frame is 1,638,400 bytes raw and 7-11 KB here, so a frame
/// costs ~3 ms on the wire instead of ~450 ms.
///
/// One deliberate omission: the reference also emits Q565_OP_DIFF_INDEXED, which searches
/// all 64 palette entries per pixel for a few percent more compression. The format does not
/// require an encoder to emit it and the frames are already tiny, so this does not.
/// </summary>
internal static class Q565Encoder
{
    private const byte OpIndex = 0b0000_0000;
    private const byte OpDiff = 0b0100_0000;
    private const byte OpLuma = 0b1000_0000;
    private const byte OpRun = 0b1100_0000;
    private const byte OpRgb565 = 0b1111_1110;
    private const byte OpEnd = 0b1111_1111;

    /// <summary>Longest run a single OP_RUN byte can carry; 63 and 64 are taken by the tags above.</summary>
    private const int MaxRun = 62;

    /// <summary>Worst case: an 8-byte header, three bytes per pixel, and the end marker.</summary>
    public static int MaxEncodedLength(int width, int height) => 8 + (width * height * 3) + 1;

    /// <summary>
    /// Encodes a packed 4-byte-per-pixel frame, rotating by whole quarter turns on the way
    /// through - the panel does not rotate what the host sends it. Set
    /// <paramref name="sourceIsBgra"/> for a capture-order frame (the panel stream is BGRA;
    /// the still-image route posts RGBA). Returns the byte count written to
    /// <paramref name="dest"/>.
    /// </summary>
    public static int Encode(
        ReadOnlySpan<byte> rgba, int width, int height, int quarterTurns, Span<byte> dest,
        bool sourceIsBgra = false)
    {
        if (rgba.Length < width * height * 4)
        {
            throw new ArgumentException("frame is shorter than width * height * 4", nameof(rgba));
        }
        if (dest.Length < MaxEncodedLength(width, height))
        {
            throw new ArgumentException("destination is too small for the worst case", nameof(dest));
        }

        int w = 0;
        dest[w++] = (byte)'q'; dest[w++] = (byte)'5'; dest[w++] = (byte)'6'; dest[w++] = (byte)'5';
        dest[w++] = (byte)(width & 0xFF);
        dest[w++] = (byte)((width >> 8) & 0xFF);
        dest[w++] = (byte)(height & 0xFF);
        dest[w++] = (byte)((height >> 8) & 0xFF);

        Span<ushort> palette = stackalloc ushort[64];
        palette.Clear();

        // The stream starts from black, so a frame that opens on black opens with a run.
        ushort prev = 0;
        byte pr = 0, pg = 0, pb = 0;
        int run = 0;
        int turns = ((quarterTurns % 4) + 4) % 4;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int sx, sy;
                switch (turns)
                {
                    case 1: sx = y; sy = height - 1 - x; break;
                    case 2: sx = width - 1 - x; sy = height - 1 - y; break;
                    case 3: sx = width - 1 - y; sy = x; break;
                    default: sx = x; sy = y; break;
                }
                int src = ((sy * width) + sx) * 4;
                byte r = (byte)(rgba[src + (sourceIsBgra ? 2 : 0)] >> 3);
                byte g = (byte)(rgba[src + 1] >> 2);
                byte b = (byte)(rgba[src + (sourceIsBgra ? 0 : 2)] >> 3);
                ushort pixel = (ushort)((r << 11) | (g << 5) | b);

                if (pixel == prev)
                {
                    if (++run == MaxRun)
                    {
                        dest[w++] = (byte)(OpRun | (MaxRun - 1));
                        run = 0;
                    }
                    continue;
                }
                if (run > 0)
                {
                    dest[w++] = (byte)(OpRun | (run - 1));
                    run = 0;
                }

                prev = pixel;
                // Hash is the two wire bytes added together, low 6 bits.
                int hash = ((pixel & 0xFF) + ((pixel >> 8) & 0xFF)) & 0x3F;

                if (palette[hash] == pixel)
                {
                    dest[w++] = (byte)(OpIndex | hash);
                    pr = r; pg = g; pb = b;
                    continue;
                }

                int dr = Diff(r, pr, 5), dg = Diff(g, pg, 6), db = Diff(b, pb, 5);
                pr = r; pg = g; pb = b;

                if (dr >= -2 && dr <= 1 && dg >= -2 && dg <= 1 && db >= -2 && db <= 1)
                {
                    // One byte already, so by the format's rule it does not enter the palette.
                    dest[w++] = (byte)(OpDiff | ((dr + 2) << 4) | ((dg + 2) << 2) | (db + 2));
                    continue;
                }

                int drg = dr - dg, dbg = db - dg;
                if (drg >= -8 && drg <= 7 && dg >= -16 && dg <= 15 && dbg >= -8 && dbg <= 7)
                {
                    dest[w++] = (byte)(OpLuma | (dg + 16));
                    dest[w++] = (byte)(((drg + 8) << 4) | (dbg + 8));
                }
                else
                {
                    dest[w++] = OpRgb565;
                    dest[w++] = (byte)(pixel & 0xFF);
                    dest[w++] = (byte)((pixel >> 8) & 0xFF);
                }

                palette[hash] = pixel;
            }
        }

        if (run > 0)
        {
            dest[w++] = (byte)(OpRun | (run - 1));
        }
        dest[w++] = OpEnd;
        return w;
    }

    /// <summary>Signed difference between two n-bit channel values, wrapping like the reference.</summary>
    private static int Diff(byte a, byte b, int bits)
    {
        int mask = (1 << bits) - 1;
        int d = (a - b) & mask;
        return d >= (1 << (bits - 1)) ? d - (1 << bits) : d;
    }
}
