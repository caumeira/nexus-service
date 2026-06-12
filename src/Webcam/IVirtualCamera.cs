using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Webcam;

public static class WebcamDefaults
{
    /// <summary>OS-visible virtual camera device name.</summary>
    public const string CameraName = "Nexus Camera";
}

/// <summary>Codec ids carried in the stream frame header flag bits.</summary>
public enum WebcamCodec
{
    H264 = 0,
    Mjpeg = 1,
}

/// <summary>How a platform camera sink can consume a given codec.</summary>
public enum WebcamCodecSupport
{
    Unsupported = 0,
    /// <summary>Encoded payloads are handed to the device as-is.</summary>
    Passthrough = 1,
    /// <summary>A decode stage is required first; no platform decoder exists yet.</summary>
    RequiresDecode = 2,
}

/// <summary>Format the virtual camera is armed with via the start endpoint.</summary>
public readonly record struct WebcamFormat(int Width, int Height, WebcamCodec Codec);

/// <summary>Per-frame metadata parsed from a stream frame header.</summary>
public readonly record struct WebcamFrameInfo(int Width, int Height, bool Keyframe, WebcamCodec Codec, uint TimestampMs);

/// <summary>
/// Platform seam for the OS virtual camera. Implementations own the device
/// lifetime; Start/Stop are viewer-driven and never run on the boot path.
/// WriteFrameAsync payload memory is pooled and only valid until the call
/// completes - copy if the device needs it longer.
/// </summary>
public interface IVirtualCamera
{
    string Name { get; }

    WebcamCodecSupport GetCodecSupport(WebcamCodec codec);

    Task StartAsync(WebcamFormat format, CancellationToken cancellationToken);

    Task StopAsync();

    ValueTask WriteFrameAsync(ReadOnlyMemory<byte> payload, in WebcamFrameInfo info);
}
