using System;
using Nexus.Service.Peripherals.Hyte.Np50;

namespace Nexus.Service.Peripherals.Hyte.Keeb;

/// <summary>
/// Pure builders/parsers for the HYTE Keeb TKL vendor-HID protocol. No IO;
/// <see cref="KeebHub"/> owns the <see cref="Nexus.Service.Peripherals.Hid.IHidDevice"/>
/// and performs the feature-report + output-report exchanges.
///
/// Commands are 9-byte HID feature reports: <c>00 RW OP 00×6</c> where RW is
/// <see cref="Write"/>/<see cref="Read"/> and OP is the opcode. Bulk payloads
/// (RGB frames, settings, layers, macros) follow as 65-byte output-report
/// pages (page byte 0 is the report id, 0x00). Wire spec:
/// hyte-refs/hyte-documents/firmware-protocol/Keeb/ and the shipping
/// OpenRGB controller (nexus-rgb/openrgb-headless/.../HYTEKeyboardController).
/// </summary>
public static class KeebProtocol
{
    // ── USB identity ──

    public const int VendorId = 0x3402;

    /// <summary>HYTE Keeb TKL (Suoai). The only PID with the documented HYTE protocol.</summary>
    public const int ProductId = 0x0300;

    /// <summary>Vendor HID collection the protocol rides on (usage page 0xFF11, usage 0xF0).</summary>
    public const int VendorUsagePage = 0xFF11;
    public const int VendorUsage = 0xF0;

    // ── Report sizes ──

    public const int FeatureReportSize = 9;
    public const int PageSize = KeebLayout.PageSize;       // 65
    public const int PageDataSize = KeebLayout.PageDataSize; // 64

    // ── Command direction bytes (feature report byte 1) ──

    public const byte Write = 0x04;
    public const byte Read = 0x84;

    // ── Opcodes (feature report byte 2) ──

    public const byte OpSettings = 0x06;
    public const byte OpProfile = 0x02;
    public const byte OpLayerKeys = 0xF2;
    public const byte OpMacro = 0xF3;
    public const byte OpLedKeyboard = 0xF0;
    public const byte OpLedSurround = 0xF1;
    public const byte OpDeviceInfo = 0x05;

    // ── Callback command bytes (interrupt-IN report byte 1) ──

    public const byte CbScrollWheel = 0x09;
    public const byte CbProfile = 0x02;
    public const byte CbSoftwareKey = 0xF1;
    public const byte CbKeyPress = 0xFA;

    /// <summary>Build a 9-byte command feature report: <c>00 rw op 00 00 00 00 00 00</c>.</summary>
    public static byte[] Feature(byte rw, byte opcode)
    {
        var buf = new byte[FeatureReportSize];
        buf[1] = rw;
        buf[2] = opcode;
        return buf;
    }

    // ── RGB streaming ──

    /// <summary>Feature report that arms a keyboard-zone (middle) RGB stream: <c>00 04 F0 …</c>.</summary>
    public static readonly byte[] KeyboardStreamFeature = Feature(Write, OpLedKeyboard);

    /// <summary>Feature report that arms an underglow-zone (surround) RGB stream: <c>00 04 F1 …</c>.</summary>
    public static readonly byte[] SurroundStreamFeature = Feature(Write, OpLedSurround);

    /// <summary>
    /// Serialise a wire buffer into <paramref name="pageCount"/> contiguous
    /// 65-byte pages (page byte 0 = report id 0x00, then R,G,B for each slot,
    /// streamed sequentially across pages). Byte-identical to OpenRGB's
    /// LEDStreaming_Keyboard / _Surround fill. Returns a <c>pageCount*65</c>
    /// buffer; the caller writes each 65-byte page as one output report.
    /// </summary>
    public static byte[] BuildStreamPages(ReadOnlySpan<RgbColor> wire, int pageCount)
    {
        var pages = new byte[pageCount * PageSize];
        var colorIdx = 0;
        var channel = 0;
        for (var pkt = 0; pkt < pageCount; pkt++)
        {
            var pageBase = pkt * PageSize;
            // pages[pageBase] is the report id (already 0).
            for (var b = 0; b < PageDataSize; b++)
            {
                var c = colorIdx < wire.Length ? wire[colorIdx] : default;
                pages[pageBase + 1 + b] = channel switch
                {
                    0 => c.R,
                    1 => c.G,
                    _ => c.B,
                };
                if (channel == 2) colorIdx++;
                channel = (channel + 1) % 3;
            }
        }
        return pages;
    }

    // ── Device info (opcode 0x05) ──

    /// <summary>Feature report requesting the device-info block.</summary>
    public static readonly byte[] DeviceInfoRequest = Feature(Read, OpDeviceInfo);

    /// <summary>Decoded device-info response.</summary>
    public readonly record struct DeviceInfo(int VendorId, int ProductId, string FirmwareVersion, string Layout);

    /// <summary>
    /// Parse the device-info response. The firmware returns a feature/input
    /// block whose first byte is the report id (0x00); the documented payload
    /// (VID, PID, FW version little-endian, layout) follows. Layout 0x01=ISO,
    /// 0x02=ANSI. Returns null on a short/garbled read.
    /// </summary>
    public static DeviceInfo? ParseDeviceInfo(ReadOnlySpan<byte> response)
    {
        // Tolerate the leading report-id byte some read paths include.
        var p = response;
        if (p.Length >= 1 && p[0] == 0x00 && p.Length > 7) p = p.Slice(1);
        if (p.Length < 7) return null;
        var vid = p[0] | (p[1] << 8);
        var pid = p[2] | (p[3] << 8);
        var fw = $"{p[5]}.{p[4]}"; // little-endian: minor at [4], major at [5] per legacy "1.33" = 0x21 0x01
        var layout = p[6] switch
        {
            0x01 => "ISO",
            0x02 => "ANSI",
            _ => "ANSI",
        };
        if (vid != VendorId) return null;
        return new DeviceInfo(vid, pid, fw, layout);
    }
}
