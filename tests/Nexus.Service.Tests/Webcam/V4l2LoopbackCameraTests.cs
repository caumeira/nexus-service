using System.Runtime.CompilerServices;
using Nexus.Service.Webcam;
using Nexus.Service.Webcam.Linux;

namespace Nexus.Service.Tests.Webcam;

public class V4l2LoopbackCameraTests
{
    private static readonly WebcamFormat Mjpeg640 = new(640, 480, WebcamCodec.Mjpeg);

    [Fact]
    public void CodecSupport_MjpegPassthrough_H264RequiresDecode()
    {
        var camera = new V4l2LoopbackCamera(new FakeV4l2DeviceIo());

        Assert.Equal(WebcamCodecSupport.Passthrough, camera.GetCodecSupport(WebcamCodec.Mjpeg));
        Assert.Equal(WebcamCodecSupport.RequiresDecode, camera.GetCodecSupport(WebcamCodec.H264));
        Assert.Equal(WebcamCodecSupport.Unsupported, camera.GetCodecSupport((WebcamCodec)7));
    }

    [Fact]
    public void SelectDeviceNode_MatchesTrimmedCardName()
    {
        var candidates = new[]
        {
            ("video0", "Integrated Camera\n"),
            ("video10", "Nexus Camera\n"),
        };

        Assert.Equal("/dev/video10", V4l2LoopbackCamera.SelectDeviceNode(candidates, "Nexus Camera"));
    }

    [Fact]
    public void SelectDeviceNode_NoMatch_ReturnsNull()
    {
        var candidates = new[] { ("video0", "Integrated Camera\n") };

        Assert.Null(V4l2LoopbackCamera.SelectDeviceNode(candidates, "Nexus Camera"));
    }

    [Fact]
    public void SelectDeviceNode_IsCaseSensitive()
    {
        var candidates = new[] { ("video0", "nexus camera\n") };

        Assert.Null(V4l2LoopbackCamera.SelectDeviceNode(candidates, "Nexus Camera"));
    }

    [Fact]
    public void BuildOutputFormat_SetsMjpegOutputFields()
    {
        var format = V4l2LoopbackCamera.BuildOutputFormat(1280, 720);

        Assert.Equal(V4l2.BufTypeVideoOutput, format.Type);
        Assert.Equal(1280u, format.Pix.Width);
        Assert.Equal(720u, format.Pix.Height);
        Assert.Equal(V4l2.PixFmtMjpeg, format.Pix.PixelFormat);
        Assert.Equal(V4l2.FieldNone, format.Pix.Field);
        Assert.Equal(0u, format.Pix.BytesPerLine);
        Assert.Equal((uint)WebcamSession.MaxFrameBytes, format.Pix.SizeImage);
    }

    [Fact]
    public void Abi_MatchesVideodev2Header64Bit()
    {
        // Known-good 64-bit kernel ABI values; if these drift, the struct
        // layout no longer matches videodev2.h and every ioctl would EINVAL.
        Assert.Equal(208, Unsafe.SizeOf<V4l2Format>());
        Assert.Equal(0xC0D05605u, (uint)V4l2.VidiocSFmt);
        Assert.Equal(0x47504A4Du, V4l2.PixFmtMjpeg);
    }

    [Fact]
    public async Task StartAsync_H264_ThrowsNotSupported()
    {
        var camera = new V4l2LoopbackCamera(new FakeV4l2DeviceIo());

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            camera.StartAsync(new WebcamFormat(640, 480, WebcamCodec.H264), CancellationToken.None));
    }

    [Fact]
    public async Task StartAsync_DiscoversDeviceOpensAndConfigures()
    {
        var io = new FakeV4l2DeviceIo();
        io.Devices.Add(("video0", "HD Webcam\n"));
        io.Devices.Add(("video7", "Nexus Camera\n"));
        var camera = new V4l2LoopbackCamera(io);

        await camera.StartAsync(Mjpeg640, CancellationToken.None);

        Assert.Equal("/dev/video7", io.OpenedPath);
        Assert.NotNull(io.ConfiguredFormat);
        Assert.Equal(640u, io.ConfiguredFormat.Value.Pix.Width);
        Assert.Equal(480u, io.ConfiguredFormat.Value.Pix.Height);
        Assert.Equal(V4l2.PixFmtMjpeg, io.ConfiguredFormat.Value.Pix.PixelFormat);
    }

    [Fact]
    public async Task StartAsync_ExplicitPathSkipsDiscovery()
    {
        var io = new FakeV4l2DeviceIo();
        var camera = new V4l2LoopbackCamera(io, "/dev/video42");

        await camera.StartAsync(Mjpeg640, CancellationToken.None);

        Assert.Equal("/dev/video42", io.OpenedPath);
    }

    [Fact]
    public async Task StartAsync_NoMatchingDevice_Throws()
    {
        var io = new FakeV4l2DeviceIo();
        io.Devices.Add(("video0", "HD Webcam\n"));
        var camera = new V4l2LoopbackCamera(io);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            camera.StartAsync(Mjpeg640, CancellationToken.None));
    }

    [Fact]
    public async Task StartAsync_OpenFailure_Throws()
    {
        var io = new FakeV4l2DeviceIo { FailOpen = true };
        var camera = new V4l2LoopbackCamera(io, "/dev/video1");

        await Assert.ThrowsAsync<IOException>(() =>
            camera.StartAsync(Mjpeg640, CancellationToken.None));
    }

    [Fact]
    public async Task StartAsync_SetFormatFailure_ClosesFdAndThrows()
    {
        var io = new FakeV4l2DeviceIo { FailSetFormat = true };
        var camera = new V4l2LoopbackCamera(io, "/dev/video1");

        await Assert.ThrowsAsync<IOException>(() =>
            camera.StartAsync(Mjpeg640, CancellationToken.None));
        Assert.Single(io.ClosedFds);
    }

    [Fact]
    public async Task WriteFrame_WritesPayloadToOpenDevice()
    {
        var io = new FakeV4l2DeviceIo();
        var camera = new V4l2LoopbackCamera(io, "/dev/video1");
        await camera.StartAsync(Mjpeg640, CancellationToken.None);

        var payload = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        await camera.WriteFrameAsync(payload, new WebcamFrameInfo(640, 480, true, WebcamCodec.Mjpeg, 0));

        var write = Assert.Single(io.Writes);
        Assert.Equal(payload, write.Payload);
    }

    [Fact]
    public async Task WriteFrame_BeforeStart_IsDropped()
    {
        var io = new FakeV4l2DeviceIo();
        var camera = new V4l2LoopbackCamera(io, "/dev/video1");

        await camera.WriteFrameAsync(new byte[] { 0x01 }, new WebcamFrameInfo(640, 480, true, WebcamCodec.Mjpeg, 0));

        Assert.Empty(io.Writes);
    }

    [Fact]
    public async Task Restart_ClosesPreviousFd()
    {
        var io = new FakeV4l2DeviceIo();
        var camera = new V4l2LoopbackCamera(io, "/dev/video1");

        await camera.StartAsync(Mjpeg640, CancellationToken.None);
        var firstFd = io.LastOpenedFd;
        await camera.StartAsync(Mjpeg640, CancellationToken.None);

        Assert.Contains(firstFd, io.ClosedFds);
    }

    [Fact]
    public async Task StopAsync_ClosesOnceAndWritesStop()
    {
        var io = new FakeV4l2DeviceIo();
        var camera = new V4l2LoopbackCamera(io, "/dev/video1");
        await camera.StartAsync(Mjpeg640, CancellationToken.None);

        await camera.StopAsync();
        await camera.StopAsync();

        Assert.Single(io.ClosedFds);
        await camera.WriteFrameAsync(new byte[] { 0x01 }, new WebcamFrameInfo(640, 480, true, WebcamCodec.Mjpeg, 0));
        Assert.Empty(io.Writes);
    }

    private sealed class FakeV4l2DeviceIo : IV4l2DeviceIo
    {
        private int _nextFd = 10;

        public List<(string Device, string CardName)> Devices { get; } = new();
        public bool FailOpen { get; set; }
        public bool FailSetFormat { get; set; }
        public string? OpenedPath { get; private set; }
        public int LastOpenedFd { get; private set; } = -1;
        public V4l2Format? ConfiguredFormat { get; private set; }
        public List<(int Fd, byte[] Payload)> Writes { get; } = new();
        public List<int> ClosedFds { get; } = new();

        public IEnumerable<(string Device, string CardName)> EnumerateVideoDevices() => Devices;

        public int Open(string path)
        {
            if (FailOpen)
                return -1;
            OpenedPath = path;
            LastOpenedFd = _nextFd++;
            return LastOpenedFd;
        }

        public bool SetFormat(int fd, ref V4l2Format format)
        {
            if (FailSetFormat)
                return false;
            ConfiguredFormat = format;
            return true;
        }

        public int Write(int fd, ReadOnlySpan<byte> payload)
        {
            Writes.Add((fd, payload.ToArray()));
            return payload.Length;
        }

        public void Close(int fd) => ClosedFds.Add(fd);
    }
}
