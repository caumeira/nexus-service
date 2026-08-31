using System;

namespace Nexus.Service.Peripherals.PixelFormats;

/// <summary>Re-orients a 4-byte-per-pixel frame for glass mounted upside down or read through a
/// reflection. Both transforms keep the frame's dimensions; a quarter turn would swap them.</summary>
public static class BgraOrientation
{
    /// <summary>True when <see cref="Apply"/> would do anything.</summary>
    public static bool IsIdentity(bool flip180, bool mirror) => !flip180 && !mirror;

    /// <summary>Writes the re-oriented frame into <paramref name="dest"/>.</summary>
    public static void Apply(
        ReadOnlySpan<byte> src, int width, int height, bool flip180, bool mirror, Span<byte> dest)
    {
        int stride = width * 4;
        if (src.Length < stride * height)
        {
            throw new ArgumentException("frame is shorter than width * height * 4", nameof(src));
        }
        if (dest.Length < stride * height)
        {
            throw new ArgumentException("destination is too small", nameof(dest));
        }

        // A 180 turn and a mirror both flip x, so together they cancel to a y-only flip.
        bool flipX = flip180 ^ mirror;
        for (int y = 0; y < height; y++)
        {
            int sy = flip180 ? height - 1 - y : y;
            var srcRow = src.Slice(sy * stride, stride);
            var destRow = dest.Slice(y * stride, stride);
            if (!flipX)
            {
                srcRow.CopyTo(destRow);
                continue;
            }
            for (int x = 0; x < width; x++)
            {
                srcRow.Slice((width - 1 - x) * 4, 4).CopyTo(destRow.Slice(x * 4, 4));
            }
        }
    }
}
