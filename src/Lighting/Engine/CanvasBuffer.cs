using System;

namespace Qos.Service.Lighting.Engine;

public sealed class CanvasBuffer
{
    private readonly byte[] _pixels;

    public CanvasBuffer(int width = 160, int height = 90)
    {
        Width = width;
        Height = height;
        _pixels = new byte[width * height * 3];
    }

    public int Width { get; }
    public int Height { get; }
    public int ByteCount => _pixels.Length;

    public void SetPixel(int x, int y, byte r, byte g, byte b)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height)
        {
            return;
        }

        var off = (y * Width + x) * 3;
        _pixels[off] = r;
        _pixels[off + 1] = g;
        _pixels[off + 2] = b;
    }

    public (byte r, byte g, byte b) GetPixel(int x, int y)
    {
        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);
        var off = (y * Width + x) * 3;
        return (_pixels[off], _pixels[off + 1], _pixels[off + 2]);
    }

    public void Fill(byte r, byte g, byte b)
    {
        for (int i = 0; i < _pixels.Length; i += 3)
        { _pixels[i] = r; _pixels[i + 1] = g; _pixels[i + 2] = b; }
    }

    public void Clear() => Array.Clear(_pixels, 0, _pixels.Length);
    public ReadOnlySpan<byte> Pixels => _pixels;

    /// <summary>
    /// Bulk write from a top-left-origin RGB888 buffer sized exactly
    /// Width * Height * 3. Used by the GPU readback path.
    /// </summary>
    public void WriteFromRgb(ReadOnlySpan<byte> src)
    {
        if (src.Length != _pixels.Length)
        {
            return;
        }
        src.CopyTo(_pixels);
    }

    /// <summary>
    /// Applies the shared hue / colorize / saturation / contrast post-process
    /// to every pixel in place. Called by Screen Mirror and Media effects so
    /// their output responds to the same four sliders the animate shaders do.
    /// </summary>
    public void ApplyPostProcess(PostProcessState state) => RgbPostProcess.Apply(_pixels, state);
}
