using System;

namespace Qos.Service.Peripherals.Hyte.MiniHub;

/// <summary>
/// Pure builders + parsers for the HYTE IBP MiniHub serial-over-USB
/// protocol. Wire spec lives in
/// hyte-refs/hyte-documents/firmware-protocol/MiniHub/main.md.
///
/// The MiniHub speaks a DIFFERENT command alphabet than NP50:
/// every control/query command uses the <c>0xFF 0xDD</c> prefix (versus
/// NP50's split of <c>0xFF 0xCC</c>/<c>0xFF 0xDD</c>/<c>0xFF 0xEE</c>),
/// and LED streaming uses <c>0xFF 0xEE 0x03</c> in <b>R G B</b> byte
/// order (versus NP50's <c>0xFF 0xEE 0x01</c> in G R B).
/// </summary>
public static class MiniHubProtocol
{
    // ── USB identity ──

    public const int VendorId = 0x3402;
    public const int ProductId = 0x0900;

    /// <summary>Two physical LED-capable ports on the hub; the spec calls them Port 3 and Port 4.</summary>
    public const int LedPortCount = 2;

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

    /// <summary>
    /// Build an LED streaming frame for one of the hub's LED channels.
    /// <paramref name="channel"/> identifies the LED port (per the spec
    /// it's a 1-based index addressing the two LED-capable ports).
    /// Bytes 0-6 are header, then 3 bytes per LED in R, G, B order.
    /// </summary>
    public static byte[] BuildLightingStream(int channel, ReadOnlySpan<RgbColor> leds)
    {
        if (channel < 1 || channel > LedPortCount)
            throw new ArgumentOutOfRangeException(nameof(channel), channel, $"Channel must be in 1..{LedPortCount}.");
        if (leds.Length > ushort.MaxValue)
            throw new ArgumentException("LED buffer too large for two-byte length field.", nameof(leds));
        var buf = new byte[7 + leds.Length * 3];
        buf[0] = Frame0; buf[1] = OpLighting; buf[2] = SubStreaming;
        buf[3] = (byte)channel;
        buf[4] = (byte)((leds.Length >> 8) & 0xFF);
        buf[5] = (byte)(leds.Length & 0xFF);
        // buf[6] reserved
        for (var i = 0; i < leds.Length; i++)
        {
            var off = 7 + i * 3;
            // R G B byte order — not GRB like NP50.
            buf[off + 0] = leds[i].R;
            buf[off + 1] = leds[i].G;
            buf[off + 2] = leds[i].B;
        }
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
