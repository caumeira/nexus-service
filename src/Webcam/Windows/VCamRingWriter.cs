using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nexus.Service.Webcam.Windows;

/// <summary>
/// Producer side of the NexusVCam shared-memory frame ring: header
/// initialization plus the per-frame seqlock publish sequence from
/// NexusVCamProtocol.h (slot seq goes odd, pixels land, seq goes even, then
/// latestSlot / frameCounter / heartbeat move). Operates over plain memory so
/// the protocol logic is unit-testable off Windows; the live camera hands it
/// the mapped section. Single writer by contract - the virtual camera
/// serializes frame writes.
/// </summary>
internal sealed class VCamRingWriter
{
    private readonly Memory<byte> _mapping;
    private readonly int _width;
    private readonly int _height;
    private readonly int _slotCount;
    private readonly int _slotBytes;
    private long _frameCounter;

    public VCamRingWriter(Memory<byte> mapping, int width, int height, int slotCount)
    {
        if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(width), "ring dimensions must be positive and even");
        if (slotCount < 1 || slotCount > VCamProtocol.MaxSlots)
            throw new ArgumentOutOfRangeException(nameof(slotCount));
        var slotBytes = VCamProtocol.Nv12FrameBytes(width, height);
        if (mapping.Length < VCamProtocol.MappingBytes(slotBytes, slotCount))
            throw new ArgumentException("mapping smaller than header plus slots", nameof(mapping));
        _mapping = mapping;
        _width = width;
        _height = height;
        _slotCount = slotCount;
        _slotBytes = slotBytes;
    }

    public void InitializeHeader(uint producerPid, long nowTickMs)
    {
        var span = _mapping.Span;
        span[..VCamProtocol.HeaderBytes].Clear();
        WriteUInt32(span, VCamProtocol.VersionOffset, VCamProtocol.Version);
        WriteUInt32(span, VCamProtocol.HeaderBytesOffset, VCamProtocol.HeaderBytes);
        WriteUInt32(span, VCamProtocol.WidthOffset, (uint)_width);
        WriteUInt32(span, VCamProtocol.HeightOffset, (uint)_height);
        WriteUInt32(span, VCamProtocol.StrideYOffset, (uint)_width);
        WriteUInt32(span, VCamProtocol.FourccOffset, VCamProtocol.FourccNv12);
        WriteUInt32(span, VCamProtocol.SlotCountOffset, (uint)_slotCount);
        WriteUInt32(span, VCamProtocol.SlotBytesOffset, (uint)_slotBytes);
        WriteUInt32(span, VCamProtocol.ProducerPidOffset, producerPid);
        Volatile.Write(ref Int64Ref(span, VCamProtocol.ProducerTickMsOffset), nowTickMs);
        _frameCounter = 0;
        // The consumer validates magic last, so it must land after the rest.
        Volatile.Write(ref Int32Ref(span, VCamProtocol.MagicOffset), unchecked((int)VCamProtocol.Magic));
    }

    /// <summary>
    /// Publishes one tight NV12 frame, letterboxing into the fixed ring
    /// geometry when sizes differ. False rejects malformed input (odd or
    /// non-positive dimensions, payload length not matching the claimed
    /// size) without touching the ring.
    /// </summary>
    public bool TryWriteFrame(ReadOnlySpan<byte> nv12, int srcWidth, int srcHeight, long nowTickMs)
    {
        if (srcWidth <= 0 || srcHeight <= 0 || (srcWidth & 1) != 0 || (srcHeight & 1) != 0)
            return false;
        if ((long)srcWidth * srcHeight * 3 / 2 != nv12.Length)
            return false;

        var span = _mapping.Span;
        var slot = (int)(_frameCounter % _slotCount);
        ref var slotSeq = ref Int32Ref(span, VCamProtocol.SlotSeqOffset + slot * sizeof(int));
        Interlocked.Increment(ref slotSeq);
        var slotSpan = span.Slice(VCamProtocol.HeaderBytes + slot * _slotBytes, _slotBytes);
        if (srcWidth == _width && srcHeight == _height)
            nv12.CopyTo(slotSpan);
        else
            Nv12Letterbox.Render(nv12, srcWidth, srcHeight, slotSpan, _width, _height);
        Interlocked.Increment(ref slotSeq);
        Volatile.Write(ref Int32Ref(span, VCamProtocol.LatestSlotOffset), slot);
        _frameCounter++;
        Volatile.Write(ref Int64Ref(span, VCamProtocol.FrameCounterOffset), _frameCounter);
        Volatile.Write(ref Int64Ref(span, VCamProtocol.ProducerTickMsOffset), nowTickMs);
        return true;
    }

    private static ref int Int32Ref(Span<byte> span, int offset) =>
        ref Unsafe.As<byte, int>(ref span[offset]);

    private static ref long Int64Ref(Span<byte> span, int offset) =>
        ref Unsafe.As<byte, long>(ref span[offset]);

    private static void WriteUInt32(Span<byte> span, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset, sizeof(uint)), value);
}
