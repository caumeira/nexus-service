using System;
using Nexus.Service.Peripherals.LianLiCp;

namespace Nexus.Service.Peripherals.Galahad2;

// Byte facts from L-Connect / FanControl.LianLi (Galahad2Protocol, Galahad2Controller).
internal static class Galahad2Protocol
{
    public const int VendorId = 0x0416;
    public const int ProductIdPerformance = 0x7371;
    public const int ProductIdRegular = 0x7373;

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
