using System;

namespace Nexus.Service.Peripherals.PixelFormats;

/// <summary>
/// Quarter-turns a 4-byte-per-pixel frame. Unlike <see cref="BgraOrientation"/>'s flips this
/// swaps the frame's dimensions, so the destination is height x width.
/// </summary>
public static class BgraQuarterTurn
{
    /// <summary>
    /// Rotates <paramref name="src"/> a quarter turn counter-clockwise into
    /// <paramref name="dest"/>, which must hold width x height pixels with the sides swapped.
    /// </summary>
    public static void RotateCcw(ReadOnlySpan<byte> src, int width, int height, Span<byte> dest)
    {
        int pixels = width * height;
        if (src.Length < pixels * 4)
        {
            throw new ArgumentException("frame is shorter than width * height * 4", nameof(src));
        }
        if (dest.Length < pixels * 4)
        {
            throw new ArgumentException("destination is too small", nameof(dest));
        }

        // Counter-clockwise sends the source's top-right corner to the destination's
        // top-left, so a source row walks up a destination column.
        int destStride = height * 4;
        for (int sy = 0; sy < height; sy++)
        {
            var srcRow = src.Slice(sy * width * 4, width * 4);
            int destX = sy * 4;
            for (int sx = 0; sx < width; sx++)
            {
                int destY = width - 1 - sx;
                srcRow.Slice(sx * 4, 4).CopyTo(dest.Slice(destY * destStride + destX, 4));
            }
        }
    }
}
