using System;

namespace Nexus.Service.Peripherals.Nzxt;

// Byte facts decoded from the NZXT Kraken Elite V2 (1E71:3012, firmware 1.2.0) on the
// bench and cross-checked against liquidctl's kraken3.py. Everything encoded here was
// exercised against real hardware; see plans/nzxt-kraken-support.md.
//
// THE trap: this firmware uses 512-byte HID reports, not the 64-byte reports liquidctl
// uses for older Krakens. A short write is rejected outright (Windows returns failure)
// while the device keeps pushing unsolicited status reports, so a caller that ignores the
// write result looks healthy while every command is dropped. Always size buffers to
// ReportLength and always check the write result.
//
// The command byte doubles as the HID report ID and replies come back on report ID+1
// (0x10 -> 0x11, 0x30 -> 0x31, 0x38 -> 0x39, 0x74 -> 0x75, ...). Report 0xFF is the
// device's NAK and echoes the rejected command in bytes [14],[15].
internal static class KrakenProtocol
{
    public const int VendorId = 0x1E71;
    public const int ProductIdKrakenEliteV2 = 0x3012;

    public const int ReportLength = 512;

    // LCD panel geometry, confirmed by reading DecodeLcdInfo off the device rather than
    // assuming it from the model name.
    public const int LcdWidth = 640;
    public const int LcdHeight = 640;

    // The bucket store is flash-backed and holds 16 slots, addressed in 1 KiB pages.
    public const int BucketCount = 16;
    public const int BucketPageBytes = 1024;

    // Pump duty floored so coolant keeps circulating; the firmware itself enforces 20.
    public const int PumpDutyFloor = 20;

    // A curve is one duty per degree from 20 C to 59 C inclusive.
    public const int CurvePointCount = 40;
    public const int CurveFirstTempC = 20;

    private const byte ReportFirmwareRequest = 0x10;
    private const byte ReportFirmwareReply = 0x11;
    private const byte ReportLightingInfoRequest = 0x20;
    private const byte ReportLightingInfoReply = 0x21;
    private const byte ReportSetColor = 0x2A;
    private const byte ReportLcdRequest = 0x30;
    private const byte ReportLcdReply = 0x31;
    private const byte ReportBucketRequest = 0x32;
    private const byte ReportBucketReply = 0x33;
    private const byte ReportTransferRequest = 0x36;
    private const byte ReportTransferReply = 0x37;
    private const byte ReportDisplayModeRequest = 0x38;
    private const byte ReportDisplayModeReply = 0x39;
    private const byte ReportStatusRequest = 0x74;
    private const byte ReportStatusReply = 0x75;
    private const byte ReportSpeedCurve = 0x72;
    private const byte ReportInit = 0x70;
    public const byte ReportNak = 0xFF;

    // Sub-commands of the 0x30 LCD family.
    private const byte LcdSubInfo = 0x01;
    private const byte LcdSubSetBacklight = 0x02;
    private const byte LcdSubReadMode = 0x03;
    private const byte LcdSubQueryBucket = 0x04;

    // Payload offset shared by every reply: 14 bytes of report id + serial + padding.
    private const int ReplyPayloadOffset = 14;

    // Byte [14] of an acknowledged reply. Any other value is a failure code.
    public const byte AckOk = 0x01;

    /// <summary>
    /// Speed channels. The trailing bytes are part of the channel identifier, not padding;
    /// both tuples were confirmed by commanding a duty and reading it back in the status report.
    /// </summary>
    public static ReadOnlySpan<byte> PumpChannel => new byte[] { 0x01, 0x01, 0x00 };

    public static ReadOnlySpan<byte> FanChannel => new byte[] { 0x02, 0x01, 0x01 };

    /// <summary>
    /// Lighting channel ids. The Elite V2 reports two channels: the pump ring and whatever
    /// RGB fans are daisy-chained off it. Ids are a bitmask, so Ring|Fans addresses both.
    /// </summary>
    public const byte ColorChannelRing = 0b001;
    public const byte ColorChannelFans = 0b010;

    // Per-channel constant the firmware uses for animation phase; from liquidctl _STATIC_VALUE.
    private static byte StaticValueFor(byte channelId) => channelId switch
    {
        ColorChannelRing => 40,
        ColorChannelFans => 8,
        _ => 40,
    };

    private static byte[] NewReport(byte reportId, byte subCommand)
    {
        var report = new byte[ReportLength];
        report[0] = reportId;
        report[1] = subCommand;
        return report;
    }

    public static byte[] EncodeFirmwareRequest() => NewReport(ReportFirmwareRequest, 0x01);

    public static byte[] EncodeStatusRequest() => NewReport(ReportStatusRequest, 0x01);

    public static byte[] EncodeLightingInfoRequest() => NewReport(ReportLightingInfoRequest, 0x03);

    // Telemetry-stream setup, sent once per connection. 0xB8 with an interval index of 1
    // is the half-second cadence liquidctl uses; the cooler will not report its accessory
    // table until reporting has been started.
    public static byte[] EncodeSetUpdateInterval()
    {
        var report = NewReport(ReportInit, 0x02);
        report[2] = 0x01;
        report[3] = 0xB8;
        report[4] = 0x01;
        return report;
    }

    public static byte[] EncodeStartReporting() => NewReport(ReportInit, 0x01);

    public static byte[] EncodeLcdInfoRequest() => NewReport(ReportLcdRequest, LcdSubInfo);

    public static byte[] EncodeReadDisplayModeRequest() => NewReport(ReportLcdRequest, LcdSubReadMode);

    public static byte[] EncodeQueryBucketRequest(int bucketIndex)
    {
        var report = NewReport(ReportLcdRequest, LcdSubQueryBucket);
        report[2] = (byte)bucketIndex;
        return report;
    }

    /// <summary>
    /// Backlight level and rotation ride the same command, so a caller changing one must
    /// pass the current value of the other or it will be overwritten.
    /// </summary>
    public static byte[] EncodeSetBacklight(int brightnessPercent, int orientationQuarterTurns)
    {
        var report = NewReport(ReportLcdRequest, LcdSubSetBacklight);
        report[2] = 0x01;
        report[3] = (byte)Math.Clamp(brightnessPercent, 0, 100);
        report[6] = 0x01;
        report[7] = (byte)(orientationQuarterTurns & 0x03);
        return report;
    }

    public static byte[] EncodeSetDisplayMode(KrakenDisplayMode mode, int bucketIndex)
    {
        var report = NewReport(ReportDisplayModeRequest, 0x01);
        report[2] = (byte)mode;
        report[3] = (byte)bucketIndex;
        return report;
    }

    public static byte[] EncodeDeleteBucket(int bucketIndex)
    {
        var report = NewReport(ReportBucketRequest, 0x02);
        report[2] = (byte)bucketIndex;
        return report;
    }

    /// <summary>
    /// Reserves <paramref name="pages"/> KiB pages at <paramref name="startPage"/> for a bucket.
    /// Callers must delete every bucket first: a stale allocation makes this return success while
    /// placing the image where the panel will never render it.
    /// </summary>
    public static byte[] EncodeSetupBucket(int bucketIndex, int startPage, int pages)
    {
        var report = NewReport(ReportBucketRequest, 0x01);
        report[2] = (byte)bucketIndex;
        report[3] = (byte)(bucketIndex + 1);
        report[4] = (byte)(startPage & 0xFF);
        report[5] = (byte)((startPage >> 8) & 0xFF);
        report[6] = (byte)(pages & 0xFF);
        report[7] = (byte)((pages >> 8) & 0xFF);
        report[8] = 0x01;
        return report;
    }

    public static byte[] EncodeStartTransfer(int bucketIndex)
    {
        var report = NewReport(ReportTransferRequest, 0x01);
        report[2] = (byte)bucketIndex;
        return report;
    }

    public static byte[] EncodeEndTransfer() => NewReport(ReportTransferRequest, 0x02);

    /// <summary>
    /// One duty per degree from 20 C to 59 C. <paramref name="duties"/> must hold
    /// <see cref="CurvePointCount"/> entries.
    /// </summary>
    public static byte[] EncodeSpeedCurve(ReadOnlySpan<byte> channel, ReadOnlySpan<byte> duties)
    {
        if (duties.Length != CurvePointCount)
        {
            throw new ArgumentException($"curve needs {CurvePointCount} points", nameof(duties));
        }
        var report = new byte[ReportLength];
        report[0] = ReportSpeedCurve;
        channel.CopyTo(report.AsSpan(1, channel.Length));
        duties.CopyTo(report.AsSpan(1 + channel.Length, CurvePointCount));
        return report;
    }

    /// <summary>
    /// Fixed-colour lighting for one channel. Colours go on the wire as GRB, not RGB.
    /// </summary>
    public static byte[] EncodeFixedColor(byte channelId, byte r, byte g, byte b)
    {
        Span<byte> one = stackalloc byte[3] { r, g, b };
        return EncodeColors(channelId, KrakenColorMode.Fixed, KrakenAnimationSpeed.Normal, one, forward: true);
    }

    /// <summary>
    /// Per-LED or animated lighting. <paramref name="rgbColors"/> is packed RGB triplets,
    /// at most <see cref="MaxColors"/> of them; this method performs the RGB to GRB swap.
    /// </summary>
    public static byte[] EncodeColors(
        byte channelId,
        KrakenColorMode mode,
        KrakenAnimationSpeed speed,
        ReadOnlySpan<byte> rgbColors,
        bool forward)
    {
        var report = new byte[ReportLength];
        report[0] = ReportSetColor;
        report[1] = 0x04;
        report[2] = channelId;
        report[3] = channelId;
        report[4] = mode.ModeByte;

        var timing = SpeedTiming(mode.SpeedScale, speed);
        report[5] = timing.Lo;
        report[6] = timing.Hi;

        int colorCount = Math.Min(rgbColors.Length / 3, MaxColors);
        for (int i = 0; i < colorCount; i++)
        {
            int src = i * 3;
            int dst = ColorBlockOffset + (i * 3);
            report[dst] = rgbColors[src + 1];     // G
            report[dst + 1] = rgbColors[src];     // R
            report[dst + 2] = rgbColors[src + 2]; // B
        }

        // Direction rides the same byte as the animation's own base value: backward adds 2.
        report[FooterOffset] = (byte)(mode.BackwardBase + (forward ? 0x00 : 0x02));
        report[FooterOffset + 1] = (byte)colorCount;
        report[FooterOffset + 2] = mode.ModeRelated;
        report[FooterOffset + 3] = StaticValueFor(channelId);
        report[FooterOffset + 4] = mode.LedSize;
        return report;
    }

    /// <summary>Per-LED addressing accepts up to this many colours per channel.</summary>
    public const int MaxDirectColors = 40;

    private const byte ReportDirect = 0x22;

    /// <summary>
    /// Builds the three reports that set every LED on a channel individually. They must be
    /// written in order: the colour table, a latch, then the command that applies it.
    /// <paramref name="rgbColors"/> is packed RGB triplets; this method does the GRB swap
    /// and zero-fills the unused slots.
    /// </summary>
    public static byte[][] EncodeDirectColors(byte channelId, ReadOnlySpan<byte> rgbColors)
    {
        var table = new byte[ReportLength];
        table[0] = ReportDirect;
        table[1] = 0x10;
        table[2] = channelId;
        table[3] = 0x00;

        int count = Math.Min(rgbColors.Length / 3, MaxDirectColors);
        for (int i = 0; i < count; i++)
        {
            int src = i * 3;
            int dst = 4 + (i * 3);
            table[dst] = rgbColors[src + 1];     // G
            table[dst + 1] = rgbColors[src];     // R
            table[dst + 2] = rgbColors[src + 2]; // B
        }

        var latch = new byte[ReportLength];
        latch[0] = ReportDirect;
        latch[1] = 0x11;
        latch[2] = channelId;

        // Trailer constants are firmware magic carried over verbatim from liquidctl's
        // super-fixed path; speed is fixed because per-LED output is not animated.
        var apply = new byte[ReportLength];
        apply[0] = ReportDirect;
        apply[1] = 0xA0;
        apply[2] = channelId;
        apply[3] = 0x00;
        apply[4] = 0x01;
        ReadOnlySpan<byte> trailer = new byte[] { 0x00, 0x00, 0x08, 0x00, 0x00, 0x80, 0x00, 0x32, 0x00, 0x00, 0x01 };
        trailer.CopyTo(apply.AsSpan(5));

        return new[] { table, latch, apply };
    }

    // Colour block starts right after the 7-byte header and holds 16 GRB triplets;
    // the 5-byte footer follows it.
    private const int ColorBlockOffset = 7;
    public const int MaxColors = 16;
    private const int FooterOffset = ColorBlockOffset + (MaxColors * 3);

    // Magic prefix every bulk (LCD pixel) transfer starts with.
    public static ReadOnlySpan<byte> BulkMagic => new byte[]
    {
        0x12, 0xFA, 0x01, 0xE8, 0xAB, 0xCD, 0xEF, 0x98, 0x76, 0x54, 0x32, 0x10,
    };

    // Wire pixel format. Only Rgba8888 is accepted by this firmware for the bucket path:
    // Rgb565 is taken without complaint and then silently ignored, the panel falling back
    // to its firmware readout. Values are the ordinals of CAM's own format enum.
    public const byte BulkFormatRgba8888 = 0x02;

    public static int LcdFrameBytes => LcdWidth * LcdHeight * 4;

    /// <summary>
    /// The 20-byte preamble that precedes the pixels. It must be written as its own bulk
    /// transfer; concatenating it with the pixel data corrupts the upload silently.
    /// </summary>
    public static byte[] EncodeBulkHeader(byte format, int payloadBytes)
    {
        var header = new byte[BulkMagic.Length + 8];
        BulkMagic.CopyTo(header);
        int i = BulkMagic.Length;
        header[i] = format;
        header[i + 4] = (byte)(payloadBytes & 0xFF);
        header[i + 5] = (byte)((payloadBytes >> 8) & 0xFF);
        header[i + 6] = (byte)((payloadBytes >> 16) & 0xFF);
        header[i + 7] = (byte)((payloadBytes >> 24) & 0xFF);
        return header;
    }

    /// <summary>Pages a bucket must reserve to hold header plus payload.</summary>
    public static int PagesFor(int payloadBytes)
    {
        int total = BulkMagic.Length + 8 + payloadBytes;
        return (total + BucketPageBytes - 1) / BucketPageBytes;
    }

    public static bool IsReplyTo(ReadOnlySpan<byte> report, byte requestReportId, byte subCommand)
    {
        return report.Length > 1 && report[0] == requestReportId + 1 && report[1] == subCommand;
    }

    public static bool IsAck(ReadOnlySpan<byte> report) =>
        report.Length > ReplyPayloadOffset && report[ReplyPayloadOffset] == AckOk;

    public static bool IsStatusReply(ReadOnlySpan<byte> report) =>
        report.Length > 1 && report[0] == ReportStatusReply;

    /// <summary>
    /// Decodes the telemetry report. The device also pushes this unsolicited about once a
    /// second with sub-command 0x02, so callers should accept any 0x75 report.
    /// </summary>
    public static KrakenReading? DecodeStatus(ReadOnlySpan<byte> report)
    {
        if (!IsStatusReply(report) || report.Length < 26)
        {
            return null;
        }
        // 0xFF 0xFF in the temperature field is the documented firmware-fault marker.
        if (report[15] == 0xFF && report[16] == 0xFF)
        {
            return null;
        }
        double liquidC = report[15] + (report[16] / 10.0);
        int pumpRpm = (report[18] << 8) | report[17];
        int fanRpm = (report[24] << 8) | report[23];
        return new KrakenReading(liquidC, pumpRpm, report[19], fanRpm, report[25]);
    }

    public static KrakenFirmware? DecodeFirmware(ReadOnlySpan<byte> report)
    {
        if (!IsReplyTo(report, ReportFirmwareRequest, 0x01) || report.Length < 0x14)
        {
            return null;
        }
        return new KrakenFirmware(report[0x11], report[0x12], report[0x13]);
    }

    public static KrakenLcdInfo? DecodeLcdInfo(ReadOnlySpan<byte> report)
    {
        if (!IsReplyTo(report, ReportLcdRequest, LcdSubInfo) || report.Length < 28)
        {
            return null;
        }
        int width = report[20] | (report[21] << 8);
        int height = report[22] | (report[23] << 8);
        return new KrakenLcdInfo(report[24], report[26], width, height);
    }

    public static KrakenDisplayMode? DecodeDisplayMode(ReadOnlySpan<byte> report)
    {
        if (!IsReplyTo(report, ReportLcdRequest, LcdSubReadMode) || report.Length <= ReplyPayloadOffset)
        {
            return null;
        }
        return (KrakenDisplayMode)report[ReplyPayloadOffset];
    }

    /// <summary>
    /// True when a queried bucket holds no asset. An occupied bucket carries its index,
    /// asset index, start page and page count from byte 14 onward.
    /// </summary>
    public static bool IsBucketEmpty(ReadOnlySpan<byte> report)
    {
        if (report.Length < 64)
        {
            return true;
        }
        // Byte 14 is the bucket's own index and is echoed even when unoccupied.
        for (int i = 15; i < 64; i++)
        {
            if (report[i] != 0)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Parses the accessory table. Byte 14 is the channel count; each channel then has
    /// <see cref="AccessorySlotsPerChannel"/> slots from byte 15, holding accessory type ids.
    /// </summary>
    public const int AccessorySlotsPerChannel = 6;

    public static int DecodeChannelCount(ReadOnlySpan<byte> report)
    {
        if (!IsReplyTo(report, ReportLightingInfoRequest, 0x03) || report.Length <= ReplyPayloadOffset)
        {
            return 0;
        }
        return report[ReplyPayloadOffset];
    }

    public static byte DecodeAccessory(ReadOnlySpan<byte> report, int channel, int slot)
    {
        int index = 15 + (channel * AccessorySlotsPerChannel) + slot;
        return index < report.Length ? report[index] : (byte)0;
    }

    /// <summary>
    /// LED count for an accessory type id. Values below are the ones this workspace has
    /// either measured or taken from OpenRGB's Hue 2 table; an unknown accessory reports 0
    /// so the caller can fall back rather than lighting a wrong-length strip.
    /// </summary>
    public static int LedCountForAccessory(byte accessoryId) => accessoryId switch
    {
        0x10 => 8,  // Kraken X3 pump ring
        0x11 => 1,  // Kraken X3 logo
        0x13 => 18, // F120 RGB
        0x14 => 18, // F140 RGB
        0x15 => 20, // F120 RGB Duo
        0x16 => 20, // F140 RGB Duo
        0x17 => 8,  // F120 RGB Core
        0x18 => 8,  // F140 RGB Core
        0x19 => 8,  // F120 RGB Core, case version
        0x1B => 16, // F240 RGB Core: two 8-LED fans
        0x1D => 24, // F360 RGB Core
        0x1E => 24, // Kraken Elite pump ring
        0x1F => 24, // F420 RGB
        _ => 0,
    };

    public static string AccessoryName(byte accessoryId) => accessoryId switch
    {
        0x10 => "Kraken Pump Ring",
        0x11 => "Kraken Logo",
        0x13 => "F120 RGB",
        0x14 => "F140 RGB",
        0x15 => "F120 RGB Duo",
        0x16 => "F140 RGB Duo",
        0x17 => "F120 RGB Core",
        0x18 => "F140 RGB Core",
        0x19 => "F120 RGB Core",
        0x1B => "F240 RGB Core",
        0x1D => "F360 RGB Core",
        0x1E => "Kraken Elite Ring",
        0x1F => "F420 RGB",
        _ => "Unknown accessory",
    };

    private readonly record struct Timing(byte Lo, byte Hi);

    // liquidctl _SPEED_VALUE: per animation scale, five timings slowest..fastest.
    private static Timing SpeedTiming(int scale, KrakenAnimationSpeed speed)
    {
        int i = (int)speed;
        return scale switch
        {
            0 => new Timing(0x32, 0x00),
            1 => i switch
            {
                0 => new Timing(0x50, 0x00),
                1 => new Timing(0x3C, 0x00),
                2 => new Timing(0x28, 0x00),
                3 => new Timing(0x14, 0x00),
                _ => new Timing(0x0A, 0x00),
            },
            2 => i switch
            {
                0 => new Timing(0x5E, 0x01),
                1 => new Timing(0x2C, 0x01),
                2 => new Timing(0xFA, 0x00),
                3 => new Timing(0x96, 0x00),
                _ => new Timing(0x50, 0x00),
            },
            5 => i switch
            {
                0 => new Timing(0x19, 0x00),
                1 => new Timing(0x14, 0x00),
                2 => new Timing(0x0F, 0x00),
                3 => new Timing(0x07, 0x00),
                _ => new Timing(0x04, 0x00),
            },
            6 => i switch
            {
                0 => new Timing(0x28, 0x00),
                1 => new Timing(0x1E, 0x00),
                2 => new Timing(0x14, 0x00),
                3 => new Timing(0x0A, 0x00),
                _ => new Timing(0x04, 0x00),
            },
            _ => new Timing(0x32, 0x00),
        };
    }
}

/// <summary>What the Kraken's LCD is currently showing.</summary>
public enum KrakenDisplayMode : byte
{
    /// <summary>Backlight on, nothing drawn - the panel reads as black.</summary>
    Blank = 0x01,
    /// <summary>The firmware's own liquid-temperature readout. Needs no host frames.</summary>
    Liquid = 0x02,
    /// <summary>Renders the contents of a stored bucket.</summary>
    Bucket = 0x04,
}

public enum KrakenAnimationSpeed
{
    Slowest = 0,
    Slower = 1,
    Normal = 2,
    Faster = 3,
    Fastest = 4,
}

/// <summary>
/// One firmware animation. <see cref="SpeedScale"/> selects which timing row the speed
/// index reads from; <see cref="ModeRelated"/> and <see cref="LedSize"/> are firmware
/// constants that vary per animation.
/// </summary>
/// <param name="BackwardBase">
/// Base value of the direction byte. Marquee animations carry 0x04 and starry-night 0x01
/// even when running forward; a backward run adds 2 on top.
/// </param>
public readonly record struct KrakenColorMode(
    byte ModeByte,
    int SpeedScale,
    byte ModeRelated,
    byte LedSize,
    byte BackwardBase = 0x00)
{
    public static readonly KrakenColorMode Off = new(0x00, 0, 0x00, 0x03);
    public static readonly KrakenColorMode Fixed = new(0x00, 0, 0x00, 0x03);
    public static readonly KrakenColorMode Fading = new(0x01, 1, 0x08, 0x03);
    public static readonly KrakenColorMode SpectrumWave = new(0x02, 2, 0x00, 0x03);
    public static readonly KrakenColorMode CoveringMarquee = new(0x04, 2, 0x00, 0x03, 0x04);
    public static readonly KrakenColorMode Pulse = new(0x06, 5, 0x08, 0x03);
    public static readonly KrakenColorMode Breathing = new(0x07, 6, 0x08, 0x03);
    public static readonly KrakenColorMode StarryNight = new(0x09, 5, 0x01, 0x03, 0x01);
    public static readonly KrakenColorMode RainbowFlow = new(0x0B, 2, 0x00, 0x03);
    public static readonly KrakenColorMode SuperRainbow = new(0x0C, 2, 0x00, 0x03);
    public static readonly KrakenColorMode RainbowPulse = new(0x0D, 2, 0x00, 0x03);
    public static readonly KrakenColorMode TaiChi = new(0x0E, 7, 0x05, 0x03);
    public static readonly KrakenColorMode WaterCooler = new(0x0F, 6, 0x05, 0x03);
    public static readonly KrakenColorMode Loading = new(0x10, 8, 0x04, 0x03);
}

public readonly record struct KrakenReading(
    double LiquidTempC,
    int PumpRpm,
    int PumpDuty,
    int FanRpm,
    int FanDuty);

public readonly record struct KrakenFirmware(int Major, int Minor, int Patch)
{
    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

public readonly record struct KrakenLcdInfo(int BrightnessPercent, int OrientationQuarterTurns, int Width, int Height);
