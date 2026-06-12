using System;

namespace Nexus.Service.Webcam.Windows;

/// <summary>One decoded frame; the memory is decoder-owned and only valid until the next decode call.</summary>
internal readonly record struct DecodedNv12Frame(ReadOnlyMemory<byte> Data, int Width, int Height);

/// <summary>
/// Seam over the platform video decoder so the virtual camera's session
/// logic is unit-testable with a fake. One encoded frame in, at most the
/// newest decoded frame out; false means the decoder buffered the input and
/// has no complete picture yet (normal while H.264 waits for a keyframe).
/// </summary>
internal interface IWindowsVideoDecoder : IDisposable
{
    bool TryDecode(ReadOnlySpan<byte> payload, uint timestampMs, out DecodedNv12Frame frame);
}

internal interface IWindowsVideoDecoderFactory
{
    IWindowsVideoDecoder Create(WebcamCodec codec, int width, int height);
}
