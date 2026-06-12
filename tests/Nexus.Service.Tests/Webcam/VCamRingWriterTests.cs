using System.Buffers.Binary;
using Nexus.Service.Webcam.Windows;

namespace Nexus.Service.Tests.Webcam;

public class VCamRingWriterTests
{
    private const int Width = 64;
    private const int Height = 36;
    private const int Slots = 3;
    private static readonly int SlotBytes = VCamProtocol.Nv12FrameBytes(Width, Height);

    private static byte[] Buffer(int slots = Slots) =>
        new byte[VCamProtocol.MappingBytes(VCamProtocol.Nv12FrameBytes(Width, Height), slots)];

    private static VCamRingWriter Writer(byte[] buffer, int slots = Slots) =>
        new(buffer, Width, Height, slots);

    private static byte[] Nv12Frame(int width, int height, byte luma, byte u, byte v)
    {
        var frame = new byte[VCamProtocol.Nv12FrameBytes(width, height)];
        frame.AsSpan(0, width * height).Fill(luma);
        var chroma = frame.AsSpan(width * height);
        for (var i = 0; i < chroma.Length; i += 2)
        {
            chroma[i] = u;
            chroma[i + 1] = v;
        }
        return frame;
    }

    private static uint ReadU32(byte[] buffer, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset, sizeof(uint)));

    private static long ReadI64(byte[] buffer, int offset) =>
        BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(offset, sizeof(long)));

    [Fact]
    public void InitializeHeader_WritesProtocolFields()
    {
        var buffer = Buffer();
        Writer(buffer).InitializeHeader(producerPid: 4242, nowTickMs: 1_000_000);

        Assert.Equal(VCamProtocol.Magic, ReadU32(buffer, VCamProtocol.MagicOffset));
        Assert.Equal(VCamProtocol.Version, ReadU32(buffer, VCamProtocol.VersionOffset));
        Assert.Equal((uint)VCamProtocol.HeaderBytes, ReadU32(buffer, VCamProtocol.HeaderBytesOffset));
        Assert.Equal((uint)Width, ReadU32(buffer, VCamProtocol.WidthOffset));
        Assert.Equal((uint)Height, ReadU32(buffer, VCamProtocol.HeightOffset));
        Assert.Equal((uint)Width, ReadU32(buffer, VCamProtocol.StrideYOffset));
        Assert.Equal(VCamProtocol.FourccNv12, ReadU32(buffer, VCamProtocol.FourccOffset));
        Assert.Equal((uint)Slots, ReadU32(buffer, VCamProtocol.SlotCountOffset));
        Assert.Equal((uint)SlotBytes, ReadU32(buffer, VCamProtocol.SlotBytesOffset));
        Assert.Equal(4242u, ReadU32(buffer, VCamProtocol.ProducerPidOffset));
        Assert.Equal(0, ReadI64(buffer, VCamProtocol.FrameCounterOffset));
        Assert.Equal(0u, ReadU32(buffer, VCamProtocol.LatestSlotOffset));
        Assert.Equal(1_000_000, ReadI64(buffer, VCamProtocol.ProducerTickMsOffset));
        for (var slot = 0; slot < VCamProtocol.MaxSlots; slot++)
            Assert.Equal(0u, ReadU32(buffer, VCamProtocol.SlotSeqOffset + slot * sizeof(int)));
    }

    [Fact]
    public void TryWriteFrame_PublishesSeqlockIndicesAndPixels()
    {
        var buffer = Buffer();
        var writer = Writer(buffer);
        writer.InitializeHeader(1, 10);
        var frame = Nv12Frame(Width, Height, luma: 200, u: 11, v: 22);

        Assert.True(writer.TryWriteFrame(frame, Width, Height, nowTickMs: 555));

        // Even seq means the slot write completed; one frame is one odd+even pair.
        Assert.Equal(2u, ReadU32(buffer, VCamProtocol.SlotSeqOffset));
        Assert.Equal(0u, ReadU32(buffer, VCamProtocol.LatestSlotOffset));
        Assert.Equal(1, ReadI64(buffer, VCamProtocol.FrameCounterOffset));
        Assert.Equal(555, ReadI64(buffer, VCamProtocol.ProducerTickMsOffset));
        var slot = buffer.AsSpan(VCamProtocol.HeaderBytes, SlotBytes);
        Assert.True(slot.SequenceEqual(frame));
    }

    [Fact]
    public void TryWriteFrame_RotatesSlotsAndAdvancesCounter()
    {
        var buffer = Buffer();
        var writer = Writer(buffer);
        writer.InitializeHeader(1, 0);
        var frame = Nv12Frame(Width, Height, 1, 2, 3);

        for (var i = 0; i < Slots + 1; i++)
            Assert.True(writer.TryWriteFrame(frame, Width, Height, i));

        Assert.Equal(Slots + 1, ReadI64(buffer, VCamProtocol.FrameCounterOffset));
        // The ring wrapped, so the first slot took two write cycles.
        Assert.Equal(4u, ReadU32(buffer, VCamProtocol.SlotSeqOffset));
        Assert.Equal(2u, ReadU32(buffer, VCamProtocol.SlotSeqOffset + sizeof(int)));
        Assert.Equal(2u, ReadU32(buffer, VCamProtocol.SlotSeqOffset + 2 * sizeof(int)));
        Assert.Equal(0u, ReadU32(buffer, VCamProtocol.LatestSlotOffset));
    }

    [Theory]
    [InlineData(0, Height)]
    [InlineData(Width, 0)]
    [InlineData(-2, Height)]
    [InlineData(63, Height)]
    [InlineData(Width, 35)]
    public void TryWriteFrame_RejectsBadDimensions(int width, int height)
    {
        var buffer = Buffer();
        var writer = Writer(buffer);
        writer.InitializeHeader(1, 0);

        Assert.False(writer.TryWriteFrame(Nv12Frame(Width, Height, 1, 2, 3), width, height, 1));
        Assert.Equal(0, ReadI64(buffer, VCamProtocol.FrameCounterOffset));
        Assert.Equal(0u, ReadU32(buffer, VCamProtocol.SlotSeqOffset));
    }

    [Fact]
    public void TryWriteFrame_RejectsPayloadLengthMismatch()
    {
        var buffer = Buffer();
        var writer = Writer(buffer);
        writer.InitializeHeader(1, 0);
        var oversize = new byte[VCamProtocol.Nv12FrameBytes(Width, Height) + 1];
        var undersize = new byte[VCamProtocol.Nv12FrameBytes(Width, Height) - 1];

        Assert.False(writer.TryWriteFrame(oversize, Width, Height, 1));
        Assert.False(writer.TryWriteFrame(undersize, Width, Height, 1));
        Assert.Equal(0, ReadI64(buffer, VCamProtocol.FrameCounterOffset));
        Assert.Equal(0u, ReadU32(buffer, VCamProtocol.SlotSeqOffset));
    }

    [Fact]
    public void TryWriteFrame_LetterboxesSmallerSourceIntoRingGeometry()
    {
        var buffer = Buffer();
        var writer = Writer(buffer);
        writer.InitializeHeader(1, 0);
        // Square source into a widescreen ring: pillarbox bars on both sides.
        var src = Nv12Frame(32, 32, luma: 200, u: 11, v: 22);

        Assert.True(writer.TryWriteFrame(src, 32, 32, 1));

        var slot = buffer.AsSpan(VCamProtocol.HeaderBytes, SlotBytes);
        var luma = slot[..(Width * Height)];
        var chroma = slot[(Width * Height)..];
        // Fit box: full height, width scaled by the same factor, centered.
        const int OutWidth = 36;
        const int Left = (Width - OutWidth) / 2;
        var midRow = luma.Slice(Height / 2 * Width, Width);
        Assert.Equal(0, midRow[Left - 1]);
        Assert.Equal(200, midRow[Left]);
        Assert.Equal(200, midRow[Left + OutWidth - 1]);
        Assert.Equal(0, midRow[Left + OutWidth]);
        var midChromaRow = chroma.Slice(Height / 4 * Width, Width);
        Assert.Equal(128, midChromaRow[Left - 2]);
        Assert.Equal(11, midChromaRow[Left]);
        Assert.Equal(22, midChromaRow[Left + 1]);
        Assert.Equal(128, midChromaRow[Left + OutWidth]);
    }

    [Fact]
    public void Constructor_RejectsUndersizedMapping()
    {
        var tooSmall = new byte[VCamProtocol.MappingBytes(SlotBytes, Slots) - 1];

        Assert.Throws<ArgumentException>(() => new VCamRingWriter(tooSmall, Width, Height, Slots));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(VCamProtocol.MaxSlots + 1)]
    public void Constructor_RejectsBadSlotCount(int slots)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VCamRingWriter(Buffer(VCamProtocol.MaxSlots), Width, Height, slots));
    }
}
