using System;

namespace Nexus.Service.Webcam.Windows;

/// <summary>
/// Repacks a decoder's contiguous output frame into tight NV12 at the
/// visible size. Decoders can emit alignment rows beyond the visible height
/// and chroma in planar or packed layouts, so each accepted subtype gets a
/// dedicated repack. Pure span code, unit-tested off Windows.
/// </summary>
internal static class Nv12Convert
{
    public static int TightBytes(int width, int height) => width * height * 3 / 2;

    /// <summary>NV12 source whose frame may carry extra alignment rows below the visible area.</summary>
    public static void FromNv12(ReadOnlySpan<byte> src, int frameWidth, int frameHeight, int width, int height, Span<byte> dst)
    {
        Validate(src, TightBytes(frameWidth, frameHeight), frameWidth, frameHeight, width, height, dst);
        CopyLuma(src, frameWidth, width, height, dst);
        var srcChroma = src[(frameWidth * frameHeight)..];
        var dstChroma = dst[(width * height)..];
        for (var y = 0; y < height / 2; y++)
            srcChroma.Slice(y * frameWidth, width).CopyTo(dstChroma.Slice(y * width, width));
    }

    /// <summary>Planar 4:2:0 source; uFirst selects I420/IYUV plane order over YV12.</summary>
    public static void FromPlanar420(ReadOnlySpan<byte> src, int frameWidth, int frameHeight, int width, int height, Span<byte> dst, bool uFirst)
    {
        Validate(src, TightBytes(frameWidth, frameHeight), frameWidth, frameHeight, width, height, dst);
        CopyLuma(src, frameWidth, width, height, dst);
        var chromaStride = frameWidth / 2;
        var planeBytes = chromaStride * (frameHeight / 2);
        var first = src.Slice(frameWidth * frameHeight, planeBytes);
        var second = src.Slice(frameWidth * frameHeight + planeBytes, planeBytes);
        var u = uFirst ? first : second;
        var v = uFirst ? second : first;
        var dstChroma = dst[(width * height)..];
        for (var y = 0; y < height / 2; y++)
        {
            var uRow = u.Slice(y * chromaStride, width / 2);
            var vRow = v.Slice(y * chromaStride, width / 2);
            var dstRow = dstChroma.Slice(y * width, width);
            for (var x = 0; x < width / 2; x++)
            {
                dstRow[x * 2] = uRow[x];
                dstRow[x * 2 + 1] = vRow[x];
            }
        }
    }

    /// <summary>Packed 4:2:2 source; chroma is vertically subsampled by taking even rows.</summary>
    public static void FromYuy2(ReadOnlySpan<byte> src, int frameWidth, int frameHeight, int width, int height, Span<byte> dst)
    {
        Validate(src, frameWidth * frameHeight * 2, frameWidth, frameHeight, width, height, dst);
        var stride = frameWidth * 2;
        for (var y = 0; y < height; y++)
        {
            var srcRow = src.Slice(y * stride, width * 2);
            var dstRow = dst.Slice(y * width, width);
            for (var x = 0; x < width; x++)
                dstRow[x] = srcRow[x * 2];
        }
        var dstChroma = dst[(width * height)..];
        for (var y = 0; y < height / 2; y++)
        {
            var srcRow = src.Slice(y * 2 * stride, width * 2);
            var dstRow = dstChroma.Slice(y * width, width);
            for (var x = 0; x < width / 2; x++)
            {
                dstRow[x * 2] = srcRow[x * 4 + 1];
                dstRow[x * 2 + 1] = srcRow[x * 4 + 3];
            }
        }
    }

    private static void CopyLuma(ReadOnlySpan<byte> src, int frameWidth, int width, int height, Span<byte> dst)
    {
        for (var y = 0; y < height; y++)
            src.Slice(y * frameWidth, width).CopyTo(dst.Slice(y * width, width));
    }

    private static void Validate(ReadOnlySpan<byte> src, int srcBytesNeeded, int frameWidth, int frameHeight, int width, int height, Span<byte> dst)
    {
        if (frameWidth <= 0 || frameHeight <= 0 || (frameWidth & 1) != 0 || (frameHeight & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(frameWidth), "frame dimensions must be positive and even");
        if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0 || width > frameWidth || height > frameHeight)
            throw new ArgumentOutOfRangeException(nameof(width), "visible dimensions must be positive, even, and within the frame");
        if (src.Length < srcBytesNeeded)
            throw new ArgumentException("source shorter than the claimed frame", nameof(src));
        if (dst.Length < TightBytes(width, height))
            throw new ArgumentException("destination shorter than the tight visible frame", nameof(dst));
    }
}
