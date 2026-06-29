using System;

namespace Nexus.Service.Peripherals.Strimer;

// Source: OpenRGB.
// HID interface MI_01, UsagePage 0xFF72, Usage 0xA1, ReportId 0xE0.
// Color order on the wire is R, B, G (green and blue are swapped vs RGB).
public static class StrimerProtocol
{
    public const int VendorId         = 0x0CF2;
    public const int ProductId        = 0xA200;
    public const int VendorUsagePage  = 0xFF72;
    public const int VendorUsage      = 0xA1;
    public const byte ReportId        = 0xE0;
    // Source: OpenRGB LianLiStrimerLConnectController.h STRIMERLCONNECT_PACKET_SIZE.
    public const int OutputReportSize = 255;

    public const int ZoneCount       = 12;
    public const int AtxZoneCount    = 6;
    public const int GpuZoneCount    = 6;
    public const int AtxLedsPerZone  = 20;
    public const int GpuLedsPerZone  = 27;
    public const int MaxLedsPerZone  = 27;

    // Wire byte for Direct/per-LED host-driven mode.
    public const byte ModeDirect = 0x01;

    // Fill report for a color push to zone. Clears the report first.
    // report[0]=ReportId, report[1]=(0x30|zone), LED data from offset 2 in R,B,G order.
    public static void WriteColorData(Span<byte> report, int zone, ReadOnlySpan<byte> leds)
    {
        report.Clear();
        report[0] = ReportId;
        report[1] = (byte)(0x30 | (zone & 0xFF));

        var dst = 2;
        var src = 0;
        while (src + 2 < leds.Length && dst + 2 < OutputReportSize)
        {
            report[dst]     = leds[src];         // R
            report[dst + 1] = leds[src + 2];     // B (swapped)
            report[dst + 2] = leds[src + 1];     // G (swapped)
            dst += 3;
            src += 3;
        }
    }

    // Returns the 7-byte effect command for zone.
    // byte[0]=ReportId, byte[1]=(0x10|zone), byte[2]=mode, byte[3]=speed, byte[4]=dir, byte[5]=brightness, byte[6]=0x00.
    public static byte[] BuildEffectCommit(int zone, byte mode, byte speed, byte dir, byte brightness)
    {
        return new byte[]
        {
            ReportId,
            (byte)(0x10 | (zone & 0xFF)),
            mode, speed, dir, brightness,
            0x00,
        };
    }

    // One apply-latch per full update cycle after all 12 zones.
    public static byte[] BuildApplyLatch()
    {
        return new byte[] { 0xE0, 0x2C, 0x0F, 0xFF, 0x00, 0x00, 0x00, 0x00 };
    }

    // 24-Pin ATX hardware zone for segment index seg (0-5).
    public static int AtxZone(int seg) => seg;

    // 8-Pin GPU hardware zone for segment index seg (0-5).
    public static int GpuZone(int seg) => seg + AtxZoneCount;
}
