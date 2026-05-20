using System;

namespace Qos.Service.Peripherals.Hyte.MiniHub;

/// <summary>
/// Pure builders + parsers for the HYTE IBP MiniHub serial-over-USB
/// protocol. Wire spec lives in
/// hyte-refs/hyte-documents/firmware-protocol/MiniHub/main.md, but
/// where the spec disagrees with HYTE's shipping nexus-control-service
/// the shipping code wins (it is what works against the actual firmware).
///
/// The MiniHub uses a different command alphabet than NP50:
/// every control/query command uses the <c>0xFF 0xDD</c> prefix (versus
/// NP50's split of <c>0xFF 0xCC</c>/<c>0xFF 0xDD</c>/<c>0xFF 0xEE</c>).
/// LED streaming uses <c>0xFF 0xEE 0x03</c> in <b>G R B</b> byte order
/// (same as NP50 — the spec doc says R G B, but
/// <c>IBPMiniHubController.SendToHardware</c> in HYTE's reference
/// implementation writes <c>color.G, color.R, color.B</c> via
/// <c>MiniHubLedStrip.ProcessColor</c>, so GRB is what the firmware
/// actually expects).
/// </summary>
public static class MiniHubProtocol
{
    // ── USB identity ──

    public const int VendorId = 0x3402;
    public const int ProductId = 0x0900;

    /// <summary>All four physical ports can carry LEDs (ports 1+2 = RGB-fan rings, ports 3+4 = LED strips).</summary>
    public const int LedPortCount = 4;

    // Fixed padded streaming buffer sizes per the reference IBPMiniHubController.
    // Header is 7 bytes; remaining bytes hold N×3 RGB triples. The firmware
    // seems to expect these exact lengths regardless of declared LED count
    // — sending shorter frames leaves unaddressed LEDs holding their last
    // colors, which manifests as random LEDs staying lit after "off".
    public const int Port4MaxLedCount = 100;            // Port 4 = "big" output (100 LEDs)
    public const int OtherPortMaxLedCount = 50;         // Ports 1, 2, 3 (50 LEDs each)
    private const int Port4PaddedLength = 7 + Port4MaxLedCount * 3;     // 307
    private const int OtherPortPaddedLength = 7 + OtherPortMaxLedCount * 3; // 157

    // ── Wire constants ──

    private const byte Frame0 = 0xFF;
    private const byte OpControl = 0xDD;    // version / mode / fan / get
    private const byte OpLighting = 0xEE;   // streaming

    private const byte SubGetFirmwareVersion = 0x02;
    private const byte SubSetRgbControlMode = 0x03;
    private const byte SubSetFanControlMode = 0x05;
    private const byte SubGetFanSpeed = 0x06;
    private const byte SubStreaming = 0x03;

    public const byte RgbModeMotherboard = 0x01;
    public const byte RgbModeSoftware = 0x00;

    // ── Builders ──

    /// <summary>Build the "Get Firmware Version" request (3 bytes).</summary>
    public static byte[] BuildGetFirmwareVersion() => new byte[] { Frame0, OpControl, SubGetFirmwareVersion };

    /// <summary>
    /// Build the "Set RGB Control Mode" request (4 bytes). <see cref="RgbModeSoftware"/>
    /// gives qos full control; <see cref="RgbModeMotherboard"/> hands off to the
    /// motherboard ARGB header (default after a power cycle).
    /// </summary>
    public static byte[] BuildSetRgbControlMode(byte mode)
    {
        if (mode != RgbModeSoftware && mode != RgbModeMotherboard)
            throw new ArgumentException($"Unknown RGB control mode 0x{mode:X2}", nameof(mode));
        return new byte[] { Frame0, OpControl, SubSetRgbControlMode, mode };
    }

    // Per HYTE's reference (IBPMiniHubController.cs:200), the two LED-count
    // header bytes are hardcoded to 0x01 0x68 (= 360) for every frame
    // regardless of the actual LED count. The MiniHub spec doc says these
    // should be the real count (LedCount_H / LedCount_L), but the shipping
    // firmware ignores that and the official agent always emits the magic
    // value — staying bug-for-bug compatible avoids any unknown-firmware-
    // quirk surprises.
    private const byte LedCountMagicHigh = 0x01;
    private const byte LedCountMagicLow = 0x68;

    /// <summary>
    /// Build an LED streaming frame for one of the hub's four channels.
    /// Always emits a fixed-length padded buffer (307 bytes for channel 4,
    /// 157 bytes for channels 1-3) matching HYTE's reference
    /// implementation. The firmware reads exactly that many bytes per
    /// frame; sending shorter buffers leaves trailing LEDs holding their
    /// previous colors and surfaces as "flicker on off". LEDs past the
    /// user-supplied count are zero-padded so they go dark.
    ///
    /// Bytes after the 7-byte header are <b>G, R, B</b> triples per LED.
    /// HYTE's shipping <c>MiniHubLedStrip.ProcessColor</c> emits exactly
    /// that order — the spec doc's "R G B" is wrong; the firmware really
    /// expects GRB.
    /// </summary>
    public static byte[] BuildLightingStream(int channel, ReadOnlySpan<RgbColor> leds)
    {
        if (channel < 1 || channel > LedPortCount)
            throw new ArgumentOutOfRangeException(nameof(channel), channel, $"Channel must be in 1..{LedPortCount}.");
        var padded = channel == 4 ? Port4PaddedLength : OtherPortPaddedLength;
        var maxLeds = channel == 4 ? Port4MaxLedCount : OtherPortMaxLedCount;
        var declaredCount = Math.Min(leds.Length, maxLeds);
        var buf = new byte[padded];
        buf[0] = Frame0; buf[1] = OpLighting; buf[2] = SubStreaming;
        buf[3] = (byte)channel;
        buf[4] = LedCountMagicHigh;
        buf[5] = LedCountMagicLow;
        // buf[6] reserved (0)
        for (var i = 0; i < declaredCount; i++)
        {
            var off = 7 + i * 3;
            buf[off + 0] = leds[i].G;
            buf[off + 1] = leds[i].R;
            buf[off + 2] = leds[i].B;
        }
        // Bytes from 7 + declaredCount*3 .. padded-1 stay zero — the new-byte[]
        // default. Acts as a deterministic "blank past N" so the firmware
        // can't keep stale colors.
        return buf;
    }

    // ── Parsers ──

    /// <summary>Parse the 7-byte firmware-version response into "Major.Minor.Build.Hw".</summary>
    public static string ParseFirmwareVersion(ReadOnlySpan<byte> response)
    {
        if (response.Length < 7) return "";
        if (response[0] != Frame0 || response[1] != OpControl || response[2] != SubGetFirmwareVersion) return "";
        return $"{response[3]}.{response[4]}.{response[5]}.{response[6]}";
    }
}

/// <summary>24-bit RGB color shared with NP50 — same wire-level RGB triple, just byte-ordered differently per device.</summary>
public readonly record struct RgbColor(byte R, byte G, byte B);
