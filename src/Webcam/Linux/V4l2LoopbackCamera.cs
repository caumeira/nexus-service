using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Webcam.Linux;

/// <summary>
/// v4l2loopback-backed virtual camera. Expects the loopback module to already
/// expose a device whose card_label matches the Nexus camera name; discovery
/// scans the sysfs video4linux class unless an explicit device node was
/// configured. MJPEG is written passthrough - v4l2loopback exposes the MJPG
/// fourcc directly and browsers/apps consume it - while H.264 needs a decode
/// stage that does not exist yet, so it is reported as RequiresDecode and the
/// session manager refuses to arm it.
/// </summary>
public sealed class V4l2LoopbackCamera : IVirtualCamera
{
    private readonly IV4l2DeviceIo _io;
    private readonly string? _explicitDevicePath;
    private readonly object _fdLock = new();
    private int _fd = -1;

    public V4l2LoopbackCamera(string? explicitDevicePath = null)
        : this(new LibcV4l2DeviceIo(), explicitDevicePath) { }

    internal V4l2LoopbackCamera(IV4l2DeviceIo io, string? explicitDevicePath = null)
    {
        _io = io;
        _explicitDevicePath = string.IsNullOrWhiteSpace(explicitDevicePath) ? null : explicitDevicePath;
    }

    public string Name => WebcamDefaults.CameraName;

    public WebcamCodecSupport GetCodecSupport(WebcamCodec codec) => codec switch
    {
        WebcamCodec.Mjpeg => WebcamCodecSupport.Passthrough,
        WebcamCodec.H264 => WebcamCodecSupport.RequiresDecode,
        _ => WebcamCodecSupport.Unsupported,
    };

    public Task StartAsync(WebcamFormat format, CancellationToken cancellationToken)
    {
        if (GetCodecSupport(format.Codec) != WebcamCodecSupport.Passthrough)
            throw new NotSupportedException("only mjpeg passthrough is supported by the Linux loopback camera");

        var device = _explicitDevicePath ?? SelectDeviceNode(_io.EnumerateVideoDevices(), Name)
            ?? throw new InvalidOperationException(
                $"no v4l2loopback device named '{Name}' found (is the v4l2loopback module loaded with card_label set?)");

        var fd = _io.Open(device);
        if (fd < 0)
            throw new IOException($"failed to open {device}");

        var fmt = BuildOutputFormat(format.Width, format.Height);
        if (!_io.SetFormat(fd, ref fmt))
        {
            _io.Close(fd);
            throw new IOException($"failed to set MJPEG output format on {device}");
        }

        lock (_fdLock)
        {
            if (_fd >= 0)
                _io.Close(_fd);
            _fd = fd;
        }
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        lock (_fdLock)
        {
            if (_fd >= 0)
            {
                _io.Close(_fd);
                _fd = -1;
            }
        }
        return Task.CompletedTask;
    }

    public ValueTask WriteFrameAsync(ReadOnlyMemory<byte> payload, in WebcamFrameInfo info)
    {
        // v4l2loopback treats each write() as one whole frame, so a short write
        // would tear it; there is no meaningful retry, the frame is just dropped.
        lock (_fdLock)
        {
            if (_fd >= 0 && payload.Length > 0)
                _io.Write(_fd, payload.Span);
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>Pure matcher: the /dev node whose sysfs card name equals the camera name.</summary>
    internal static string? SelectDeviceNode(IEnumerable<(string Device, string CardName)> candidates, string cameraName)
    {
        foreach (var (device, cardName) in candidates)
        {
            if (string.Equals(cardName.Trim(), cameraName, StringComparison.Ordinal))
                return "/dev/" + device;
        }
        return null;
    }

    /// <summary>Pure builder for the output S_FMT call: single-planar MJPEG, progressive.</summary>
    internal static V4l2Format BuildOutputFormat(int width, int height) => new()
    {
        Type = V4l2.BufTypeVideoOutput,
        Pix = new V4l2PixFormat
        {
            Width = (uint)width,
            Height = (uint)height,
            PixelFormat = V4l2.PixFmtMjpeg,
            Field = V4l2.FieldNone,
            // Compressed stream: no fixed stride; sizeimage advertises the max
            // frame a consumer should expect, bounded by the transport frame cap.
            BytesPerLine = 0,
            SizeImage = WebcamSession.MaxFrameBytes,
        },
    };
}
