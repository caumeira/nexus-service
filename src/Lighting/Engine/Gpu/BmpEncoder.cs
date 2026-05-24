using System;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>
/// Minimal uncompressed 24-bpp BMP encoder. No NuGet, no AOT/trim warnings.
/// Accepts a top-left-origin RGB888 span, writes a BMP with the required
/// BGR-swap, 4-byte row padding, and bottom-up row order.
/// </summary>
internal static class BmpEncoder
{
    public static byte[] Encode(ReadOnlySpan<byte> rgb, int width, int height)
    {
        int srcStride = width * 3;
        int rowPad = (4 - (srcStride % 4)) % 4;
        int dstStride = srcStride + rowPad;
        int pixelBytes = dstStride * height;
        int fileSize = 54 + pixelBytes;

        var buf = new byte[fileSize];
        // BITMAPFILEHEADER
        buf[0] = (byte)'B';
        buf[1] = (byte)'M';
        WriteInt32(buf, 2, fileSize);
        // 4 reserved bytes stay zero
        WriteInt32(buf, 10, 54); // pixel data offset

        // BITMAPINFOHEADER (40 bytes)
        WriteInt32(buf, 14, 40);
        WriteInt32(buf, 18, width);
        WriteInt32(buf, 22, height);         // positive = bottom-up
        WriteInt16(buf, 26, 1);              // planes
        WriteInt16(buf, 28, 24);             // bits per pixel
        WriteInt32(buf, 30, 0);              // BI_RGB
        WriteInt32(buf, 34, pixelBytes);
        WriteInt32(buf, 38, 2835);           // ~72 DPI horizontal
        WriteInt32(buf, 42, 2835);           // ~72 DPI vertical
        WriteInt32(buf, 46, 0);              // colours used
        WriteInt32(buf, 50, 0);              // important colours

        // Pixel data: BMP is BGR and bottom-up. Our source is RGB top-down.
        for (int y = 0; y < height; y++)
        {
            int srcY = height - 1 - y;
            int srcBase = srcY * srcStride;
            int dstBase = 54 + y * dstStride;
            for (int x = 0; x < width; x++)
            {
                int s = srcBase + x * 3;
                int d = dstBase + x * 3;
                buf[d] = rgb[s + 2]; // B
                buf[d + 1] = rgb[s + 1]; // G
                buf[d + 2] = rgb[s];     // R
            }
            // row padding stays zero
        }
        return buf;
    }

    private static void WriteInt16(byte[] buf, int off, short v)
    {
        buf[off] = (byte)(v & 0xFF);
        buf[off + 1] = (byte)((v >> 8) & 0xFF);
    }

    private static void WriteInt32(byte[] buf, int off, int v)
    {
        buf[off] = (byte)(v & 0xFF);
        buf[off + 1] = (byte)((v >> 8) & 0xFF);
        buf[off + 2] = (byte)((v >> 16) & 0xFF);
        buf[off + 3] = (byte)((v >> 24) & 0xFF);
    }
}
