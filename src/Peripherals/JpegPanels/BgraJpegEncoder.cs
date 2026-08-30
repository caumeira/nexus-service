using System;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace Nexus.Service.Peripherals.JpegPanels;

/// <summary>
/// Turns one captured BGRA frame into the JPEG these panels take.
///
/// The overlay hands frames back in capture order (BGRA, <c>DXGI_FORMAT_B8G8R8A8_UNORM</c>),
/// which is exactly ImageSharp's <see cref="Bgra32"/> layout, so there is no channel swap
/// here - the bytes are reinterpreted, not rearranged. Encoding lives on this side rather
/// than in the overlay because the overlay has no image library at all (WebView2 only), and
/// adding one to a native-AOT Windows binary would make this path untestable off Windows.
///
/// Not thread-safe: one instance per stream transport, which is the only caller.
/// </summary>
public sealed class BgraJpegEncoder : IDisposable
{
    /// <summary>
    /// Matches <c>RenderKit.JpegQuality</c>. These panels are small and the wire is a
    /// 1 KB-at-a-time HID pipe, so quality trades directly against frame time.
    /// </summary>
    public const int Quality = 85;

    private readonly int _width;
    private readonly int _height;
    private readonly JpegEncoder _encoder;
    private readonly MemoryStream _buffer;
    private bool _disposed;

    public BgraJpegEncoder(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "panel size must be positive");
        }
        _width = width;
        _height = height;
        _encoder = new JpegEncoder { Quality = Quality };
        // Grows to whatever the busiest frame needs and then stops reallocating.
        _buffer = new MemoryStream(64 * 1024);
    }

    public int FrameBytes => _width * _height * 4;

    /// <summary>
    /// Encodes one frame. The returned span points into this encoder's own buffer and is
    /// valid only until the next call - callers write it to the wire before encoding again.
    /// </summary>
    public ReadOnlySpan<byte> Encode(ReadOnlySpan<byte> bgra)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (bgra.Length < FrameBytes)
        {
            throw new ArgumentException($"frame must be at least {FrameBytes} bytes", nameof(bgra));
        }
        using var image = Image.LoadPixelData<Bgra32>(bgra[..FrameBytes], _width, _height);
        _buffer.SetLength(0);
        image.Save(_buffer, _encoder);
        return _buffer.GetBuffer().AsSpan(0, (int)_buffer.Length);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _buffer.Dispose();
    }
}
