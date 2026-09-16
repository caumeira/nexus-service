using System;
using Nexus.Service.Peripherals.LianLiCp;

namespace Nexus.Service.Peripherals.Galahad2;

// Byte facts from L-Connect / FanControl.LianLi (Galahad2Protocol, Galahad2Controller).
internal static class Galahad2Protocol
{
    public const int VendorId = 0x0416;
    public const int ProductIdPerformance = 0x7371;
    public const int ProductIdRegular = 0x7373;
    public const int ProductIdLcd = 0x7395;

    // The pump ring has 24 physical LEDs, but only 12 are independently addressable -
    // the other 12 mirror them. Confirmed against real hardware (also matches what
    // SignalRGB exposes for this pump), not a guess.
    public const int PumpLedCount = 12;
    public const int PerLedReportLength = 1024;

    // Pump duty floored to keep coolant circulating; never command below this.
    public const int PumpDutyFloor = 50;

    private const byte CmdSetFan = 0x8B;
    private const byte CmdSetPump = 0x8A;
    private const byte CmdHandshake = 0x81;

    // syncFlag byte 0x00 = host control (not sync-to-mobo).
    public static byte[] EncodeSetFan(int dutyPercent)
    {
        byte duty = (byte)Math.Clamp(dutyPercent, 0, 100);
        return CommandPacket.Build(CmdSetFan, 0x00, duty);
    }

    // Encoder floors pump at PumpDutyFloor; the provider also clamps before calling this.
    public static byte[] EncodeSetPump(int dutyPercent)
    {
        byte duty = (byte)Math.Clamp(dutyPercent, PumpDutyFloor, 100);
        return CommandPacket.Build(CmdSetPump, 0x00, duty);
    }

    public static byte[] EncodeHandshakeRequest() =>
        CommandPacket.Build(CmdHandshake);

    private const byte CmdRgbControl = 0x83;
    private const byte CmdPerLed = 0x14;
    // Declared payload length for RGB packets; matches 0x13 from the wire.
    private const int RgbPayloadLength = 19;

    private const int PumpPerLedDataLength = 61;
    private const int PerLedHeaderLength = 11;
    private const int PumpPerLedPrefixLength = 24;

    // Payload layout from OpenRGB LianLiGAIITrinityController.cpp:
    // [ring, mode, brightness, speed, R0,G0,B0, R1,G1,B1, R2,G2,B2, R3,G3,B3, direction].
    // Color order is R,G,B (no swap). Ring: inner=0, outer=1, both=2.
    // colors span carries up to 4*(R,G,B) = 12 bytes; extra slots stay zero.
    public static byte[] EncodeLighting(byte ring, byte mode, byte brightness, byte speed, byte direction, ReadOnlySpan<byte> colors)
    {
        var payload = new byte[RgbPayloadLength];
        payload[0] = ring;
        payload[1] = mode;
        payload[2] = brightness;
        payload[3] = speed;
        var colorBytes = Math.Min(colors.Length, 12);
        for (var i = 0; i < colorBytes; i++)
        {
            payload[4 + i] = colors[i];
        }
        payload[16] = direction;
        return CommandPacket.Build(CmdRgbControl, payload);
    }

    // The LCD variant uses a separate 1024-byte B-report for the pump's individually
    // addressable positions. The UI order is reversed on the wire by the controller.
    //
    // Provenance: this 61-byte data length / 24-byte zero prefix / reversed LED order
    // is NOT in lian-li-linux or OpenRGB - checked both. lian-li-linux's AIO LCD pump
    // path (hydroshift_lcd/rgb.rs) only drives the same 0x83 zone/effect command
    // EncodeLighting already sends, over a 64-byte A-report; it has no per-LED B-report
    // command at all. This layout came out of an AI-assisted reverse-engineering pass
    // with no capture trail kept, so it can't be cited to a source - what backs it
    // instead is a direct hardware check: canvas colours visibly track live on the pump
    // head on a real Galahad II LCD (recorded on video), matching what this function
    // sends. Treat the exact byte layout as verified-by-observation, not documented -
    // if a real capture ever surfaces, replace this note with that citation.
    //
    // Writes into `destination` (caller-owned, reused across ticks at ~30 fps) rather
    // than allocating a fresh 1024-byte packet every call.
    public static void EncodePumpPerLed(ReadOnlySpan<byte> colors, Span<byte> destination)
    {
        destination[..PerLedReportLength].Clear();
        destination[0] = 0x02;
        destination[1] = CmdPerLed;
        WriteUInt32BigEndian(destination.Slice(2, 4), PumpPerLedDataLength);
        // bytes 6..8 are the 24-bit packet sequence; the first and only packet is zero.
        destination[9] = (byte)(PumpPerLedDataLength >> 8);
        destination[10] = (byte)PumpPerLedDataLength;

        // data[0] is the pump zone. data[1..24] is a controller-required zero prefix.
        destination[PerLedHeaderLength] = 0x00;
        var colorBytes = Math.Min(colors.Length, PumpLedCount * 3);
        for (var position = 0; position < PumpLedCount; position++)
        {
            var source = position * 3;
            var target = PerLedHeaderLength + 1 + PumpPerLedPrefixLength
                + (PumpLedCount - 1 - position) * 3;
            if (source + 2 >= colorBytes)
            {
                continue;
            }
            destination[target] = colors[source];
            destination[target + 1] = colors[source + 1];
            destination[target + 2] = colors[source + 2];
        }
    }

    // Reply payload (4 bytes): [fanRpm_hi, fanRpm_lo, pumpRpm_hi, pumpRpm_lo] (BE16 each).
    // Returns null when the payload is too short to decode.
    public static Galahad2Reading? DecodeHandshake(ReadOnlySpan<byte> packet)
    {
        if (CommandPacket.PayloadLengthOf(packet) < 4)
        {
            return null;
        }
        int offset = CommandPacket.PayloadOffset;
        int fanRpm = (packet[offset] << 8) | packet[offset + 1];
        int pumpRpm = (packet[offset + 2] << 8) | packet[offset + 3];
        return new Galahad2Reading(fanRpm, pumpRpm);
    }

    private static void WriteUInt32BigEndian(Span<byte> destination, int value)
    {
        destination[0] = (byte)((value >> 24) & 0xFF);
        destination[1] = (byte)((value >> 16) & 0xFF);
        destination[2] = (byte)((value >> 8) & 0xFF);
        destination[3] = (byte)(value & 0xFF);
    }
}

public readonly struct Galahad2Reading
{
    public Galahad2Reading(int fanRpm, int pumpRpm)
    {
        FanRpm = fanRpm;
        PumpRpm = pumpRpm;
    }

    public int FanRpm { get; }
    public int PumpRpm { get; }
}
