using System;

namespace Nexus.Service.Peripherals.PixelFormats;

/// <summary>
/// Packs a 4-byte-per-pixel frame into little-endian RGB565. Two unrelated panels want it:
/// the 2023 NZXT Kraken (0x300E), whose firmware has no Q565 decoder, and Thermalright's
/// Frozen Warframe Pro. Small panels both, so an uncompressed frame is affordable.
///
/// Rotation rides the read index rather than costing a separate pass: no panel that takes
/// this format re-orients what the host sends it.
/// </summary>
public static class Rgb565Encoder
{
    public static int EncodedLength(int width, int height) => width * height * 2;

    /// <summary>
    /// Returns the byte count written to <paramref name="dest"/>. Set
    /// <paramref name="sourceIsBgra"/> for a capture-order frame.
    /// </summary>
    public static int Encode(
        ReadOnlySpan<byte> rgba, int width, int height, int quarterTurns, Span<byte> dest,
        bool sourceIsBgra = false)
    {
        if (rgba.Length < width * height * 4)
        {
            throw new ArgumentException("frame is shorter than width * height * 4", nameof(rgba));
        }
        if (dest.Length < EncodedLength(width, height))
        {
            throw new ArgumentException("destination is too small", nameof(dest));
        }

        int turns = ((quarterTurns % 4) + 4) % 4;
        int w = 0;
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
                int r = rgba[src + (sourceIsBgra ? 2 : 0)] >> 3;
                int g = rgba[src + 1] >> 2;
                int b = rgba[src + (sourceIsBgra ? 0 : 2)] >> 3;
                int pixel = (r << 11) | (g << 5) | b;
                dest[w++] = (byte)(pixel & 0xFF);
                dest[w++] = (byte)((pixel >> 8) & 0xFF);
            }
        }
        return w;
    }
}
