using System.Buffers.Binary;
using Nexus.Service.Webcam;

namespace Nexus.Service.Tests.Webcam;

public class WebcamFrameParserTests
{
    internal static byte[] BuildFrame(
        byte version = WebcamSession.ProtocolVersion,
        bool keyframe = false,
        int codecBits = 0,
        int width = 1280,
        int height = 720,
        uint timestampMs = 0,
        int payloadLength = 4)
    {
        var buf = new byte[WebcamSession.HeaderLength + payloadLength];
        buf[0] = version;
        buf[1] = (byte)((keyframe ? 1 : 0) | (codecBits << 1));
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), (ushort)height);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(6), timestampMs);
        for (var i = 0; i < payloadLength; i++)
            buf[WebcamSession.HeaderLength + i] = (byte)(i + 1);
        return buf;
    }

    [Fact]
    public void Parse_ValidH264Keyframe()
    {
        var frame = BuildFrame(keyframe: true, codecBits: 0, width: 1920, height: 1080, timestampMs: 123456789);

        var error = WebcamSession.TryParseFrame(frame, out var info, out var payloadOffset);

        Assert.Equal(WebcamFrameError.None, error);
        Assert.Equal(WebcamSession.HeaderLength, payloadOffset);
        Assert.Equal(1920, info.Width);
        Assert.Equal(1080, info.Height);
        Assert.True(info.Keyframe);
        Assert.Equal(WebcamCodec.H264, info.Codec);
        Assert.Equal(123456789u, info.TimestampMs);
    }

    [Fact]
    public void Parse_ValidMjpegDeltaFrame()
    {
        var frame = BuildFrame(keyframe: false, codecBits: 1, width: 640, height: 480, timestampMs: 42);

        var error = WebcamSession.TryParseFrame(frame, out var info, out _);

        Assert.Equal(WebcamFrameError.None, error);
        Assert.False(info.Keyframe);
        Assert.Equal(WebcamCodec.Mjpeg, info.Codec);
        Assert.Equal(640, info.Width);
        Assert.Equal(480, info.Height);
        Assert.Equal(42u, info.TimestampMs);
    }

    [Fact]
    public void Parse_ReservedFlagBitsAreIgnored()
    {
        var frame = BuildFrame(codecBits: 1);
        frame[1] |= 0b1111_1000;

        var error = WebcamSession.TryParseFrame(frame, out var info, out _);

        Assert.Equal(WebcamFrameError.None, error);
        Assert.Equal(WebcamCodec.Mjpeg, info.Codec);
    }

    [Fact]
    public void Parse_EmptyPayloadIsValid()
    {
        var frame = BuildFrame(payloadLength: 0);

        var error = WebcamSession.TryParseFrame(frame, out _, out var payloadOffset);

        Assert.Equal(WebcamFrameError.None, error);
        Assert.Equal(frame.Length, payloadOffset);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x02)]
    [InlineData(0xFF)]
    public void Parse_RejectsUnknownVersion(byte version)
    {
        var frame = BuildFrame(version: version);

        Assert.Equal(WebcamFrameError.UnknownVersion, WebcamSession.TryParseFrame(frame, out _, out _));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Parse_RejectsUnknownCodec(int codecBits)
    {
        var frame = BuildFrame(codecBits: codecBits);

        Assert.Equal(WebcamFrameError.UnknownCodec, WebcamSession.TryParseFrame(frame, out _, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(WebcamSession.HeaderLength - 1)]
    public void Parse_RejectsTruncatedHeader(int length)
    {
        var frame = new byte[length];
        if (length > 0)
            frame[0] = WebcamSession.ProtocolVersion;

        Assert.Equal(WebcamFrameError.TruncatedHeader, WebcamSession.TryParseFrame(frame, out _, out _));
    }

    [Fact]
    public void Parse_RejectsOversizeMessage()
    {
        var frame = BuildFrame(codecBits: 1, payloadLength: WebcamSession.MaxFrameBytes - WebcamSession.HeaderLength + 1);

        Assert.Equal(WebcamFrameError.Oversize, WebcamSession.TryParseFrame(frame, out _, out _));
    }

    [Fact]
    public void Parse_AcceptsMaxSizeMessage()
    {
        var frame = BuildFrame(codecBits: 1, payloadLength: WebcamSession.MaxFrameBytes - WebcamSession.HeaderLength);

        Assert.Equal(WebcamFrameError.None, WebcamSession.TryParseFrame(frame, out _, out _));
    }
}
