using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Nexus.Service.Peripherals.Tryx.Panorama;

/// <summary>
/// Hand-rolled protobuf writer for the RK-firmware Panorama (VID 0x391A, "RK PANO").
/// A frame is ASCII "TRYX" followed by a little-endian uint32 payload length, followed
/// by the protobuf body; tags are (fieldNumber &lt;&lt; 3) | wireType varints. Camera-verified
/// against the physical panel. Only the heartbeat and brightness commands are decoded;
/// the rest of the schema is unknown, so this class exposes nothing else.
/// </summary>
public static class TryxRkProtocol
{
    private static readonly byte[] FrameMagic = Encoding.ASCII.GetBytes("TRYX");

    /// <summary>
    /// Session keep-alive. The panel drops the screen to standby after about 10
    /// seconds without one, so the caller must resend on roughly a 1 Hz cadence.
    /// Field 1 is an empty submessage; field 10 carries a fixed "hello?" string.
    /// </summary>
    public static byte[] BuildHeartbeat()
    {
        var payload = new List<byte>();
        WriteLengthDelimited(payload, fieldNumber: 1, Array.Empty<byte>());

        var greeting = new List<byte>();
        WriteLengthDelimited(greeting, fieldNumber: 1, Encoding.ASCII.GetBytes("hello?"));
        WriteLengthDelimited(payload, fieldNumber: 10, greeting.ToArray());

        return WrapFrame(payload);
    }

    /// <summary>
    /// Minimal brightness write, camera-verified not to disturb the currently
    /// playing media or any other panel state. Field 200 nests field 5, which
    /// carries field 1 (a fixed selector) and field 2 (the brightness percent).
    /// </summary>
    public static byte[] BuildBrightness(int brightnessPercent)
    {
        var clamped = Math.Clamp(brightnessPercent, 0, 100);

        var selector = new List<byte>();
        WriteVarintField(selector, fieldNumber: 1, 1);
        WriteVarintField(selector, fieldNumber: 2, (ulong)clamped);

        var brightnessBlock = new List<byte>();
        WriteLengthDelimited(brightnessBlock, fieldNumber: 5, selector.ToArray());

        var payload = new List<byte>();
        WriteLengthDelimited(payload, fieldNumber: 1, Array.Empty<byte>());
        WriteLengthDelimited(payload, fieldNumber: 200, brightnessBlock.ToArray());

        return WrapFrame(payload);
    }

    private static byte[] WrapFrame(List<byte> payload)
    {
        var frame = new byte[FrameMagic.Length + 4 + payload.Count];
        FrameMagic.CopyTo(frame, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(FrameMagic.Length, 4), (uint)payload.Count);
        payload.CopyTo(frame, FrameMagic.Length + 4);
        return frame;
    }

    private static void WriteVarintField(List<byte> buf, int fieldNumber, ulong value)
    {
        WriteTag(buf, fieldNumber, wireType: 0);
        WriteVarint(buf, value);
    }

    private static void WriteLengthDelimited(List<byte> buf, int fieldNumber, byte[] value)
    {
        WriteTag(buf, fieldNumber, wireType: 2);
        WriteVarint(buf, (ulong)value.Length);
        buf.AddRange(value);
    }

    private static void WriteTag(List<byte> buf, int fieldNumber, int wireType)
        => WriteVarint(buf, ((ulong)(uint)fieldNumber << 3) | (uint)wireType);

    private static void WriteVarint(List<byte> buf, ulong value)
    {
        while (value >= 0x80)
        {
            buf.Add((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        buf.Add((byte)value);
    }
}
