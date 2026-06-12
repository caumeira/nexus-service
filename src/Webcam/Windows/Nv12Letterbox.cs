using System;

namespace Nexus.Service.Webcam.Windows;

/// <summary>
/// Nearest-neighbor aspect-preserving blit of a tight NV12 frame into a
/// fixed-size NV12 destination, with black bars filling the remainder. The
/// ring consumer's media type is pinned, so arbitrary phone frame sizes
/// (including portrait and mid-stream rotation) are fitted here instead of
/// renegotiating the consumer's format.
/// </summary>
internal static class Nv12Letterbox
{
    private const byte ChromaNeutral = 128;

    /// <summary>Destination must be a whole tight NV12 frame; it is fully overwritten.</summary>
    public static void Render(ReadOnlySpan<byte> src, int srcWidth, int srcHeight, Span<byte> dst, int dstWidth, int dstHeight)
    {
        var dstLumaBytes = dstWidth * dstHeight;
        dst[..dstLumaBytes].Clear();
        dst.Slice(dstLumaBytes, dstLumaBytes / 2).Fill(ChromaNeutral);

        // Fit box scaled to the tighter axis; dimensions and offsets stay
        // even so the interleaved chroma plane aligns to pixel pairs.
        int outWidth, outHeight;
        if ((long)srcWidth * dstHeight >= (long)srcHeight * dstWidth)
        {
            outWidth = dstWidth;
            outHeight = (int)((long)srcHeight * dstWidth / srcWidth) & ~1;
        }
        else
        {
            outHeight = dstHeight;
            outWidth = (int)((long)srcWidth * dstHeight / srcHeight) & ~1;
        }
        outWidth = Math.Max(2, outWidth);
        outHeight = Math.Max(2, outHeight);
        var left = ((dstWidth - outWidth) / 2) & ~1;
        var top = ((dstHeight - outHeight) / 2) & ~1;

        // One column map shared by both planes; chroma pairs columns.
        var columnMap = new int[outWidth];
        for (var x = 0; x < outWidth; x++)
            columnMap[x] = (int)((long)x * srcWidth / outWidth);

        for (var y = 0; y < outHeight; y++)
        {
            var srcRow = src.Slice((int)((long)y * srcHeight / outHeight) * srcWidth, srcWidth);
            var dstRow = dst.Slice((top + y) * dstWidth + left, outWidth);
            for (var x = 0; x < outWidth; x++)
                dstRow[x] = srcRow[columnMap[x]];
        }

        var srcChroma = src[(srcWidth * srcHeight)..];
        var dstChroma = dst[dstLumaBytes..];
        for (var y = 0; y < outHeight / 2; y++)
        {
            var srcRow = srcChroma.Slice((int)((long)y * (srcHeight / 2) / (outHeight / 2)) * srcWidth, srcWidth);
            var dstRow = dstChroma.Slice((top / 2 + y) * dstWidth + left, outWidth);
            for (var x = 0; x < outWidth / 2; x++)
            {
                var srcPair = columnMap[x * 2] & ~1;
                dstRow[x * 2] = srcRow[srcPair];
                dstRow[x * 2 + 1] = srcRow[srcPair + 1];
            }
        }
    }
}
