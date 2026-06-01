using System;

namespace Nexus.Service.Peripherals.Hyte.SmartHub;

/// <summary>
/// Pure builders + parsers for the HYTE Smart Hub serial-over-USB protocol
/// — a simple ARGB + PWM-fan hub (4 ARGB ports + 4 PWM-fan ports).
///
/// Every behaviour pinned here mirrors HYTE's shipping nexus-control-service
/// (the working production agent against the same firmware):
///   • <c>LightDancing/Hardware/Devices/HYTE/Hub/ControlHubController.cs</c>
///   • <c>LightDancing/Common/SmartDeviceCommon/Command/ControlHubCommand.cs</c>
/// where the legacy enum/device name for this VID/PID is
/// <c>USBDevices.ControlHub</c> / "HYTE Smart Hub".
///
/// The Smart Hub command alphabet is the same family NP50 uses:
/// <c>0xFF 0xCC</c> for control/query, <c>0xFF 0xDD</c> for firmware
/// version, <c>0xFF 0xEE</c> for LED streaming. LED bytes go out in
/// <b>G R B</b> order (HYTE's <c>LedStrip.ProcessColor</c> emits
/// <c>{ color.G, color.R, color.B }</c>, same as the MiniHub / NP50 rings).
/// </summary>
public static class SmartHubProtocol
{
    // ── USB identity ──

    public const int VendorId = 0x3402;
    public const int ProductId = 0x0904;

    /// <summary>Number of PWM fan ports (0-indexed 0..3 on the wire).</summary>
    public const int FanChannelCount = 4;

    /// <summary>Number of ARGB ports (1-indexed 1..4 on the wire).</summary>
    public const int ArgbPortCount = 4;

    /// <summary>
    /// Firmware-accepted LED ceiling per ARGB port. The reference
    /// <c>ControlHubDeviceBase.MAX_SUPPORT_LED_EACH_PORT</c> is 200, and
    /// <c>SendToHardware</c> pads every streamed frame to exactly that many
    /// LEDs (200×3 = 600 colour bytes) regardless of the declared count, so
    /// un-addressed LEDs go dark instead of holding a stale colour.
    /// </summary>
    public const int MaxLedsPerPort = 200;

    // ── Wire constants ──

    private const byte Frame0 = 0xFF;
    private const byte OpControl = 0xCC;    // info / fan speed / fw-animation
    private const byte OpVersion = 0xDD;    // firmware version
    private const byte OpLighting = 0xEE;   // LED streaming

    private const byte SubGetVersion = 0x02;       // FF DD 02
    private const byte SubGetInfo = 0x01;           // FF CC 01 00
    private const byte SubSetFanSpeed = 0x02;       // FF CC 02 <ch> <pct> <en>
    private const byte SubSetFwAnimation = 0x07;    // FF CC 07 <0 on | 1 off>
    private const byte SubStreaming = 0x01;          // FF EE 01 <port> ...

    /// <summary>20-byte response to <see cref="BuildGetInfo"/> carrying all four channels' tach + enabled flags.</summary>
    public const int GetInfoResponseLength = 20;

    /// <summary>7-byte response to <see cref="BuildGetFirmwareVersion"/>.</summary>
    public const int FirmwareVersionResponseLength = 7;

    /// <summary>Streamed frame = 7-byte header + MaxLedsPerPort×3 colour bytes.</summary>
    public const int LightingFrameLength = 7 + MaxLedsPerPort * 3; // 607

    public const int FanMinDutyPercent = 0;
    public const int FanMaxDutyPercent = 100;

    // ── Builders ──

    /// <summary>"Get Firmware Version" request (3 bytes).</summary>
    public static byte[] BuildGetFirmwareVersion() => new byte[] { Frame0, OpVersion, SubGetVersion };

    /// <summary>
    /// "Get Hub Info" request (4 bytes). The 20-byte reply carries every
    /// channel's tach + enabled byte in one shot — the reference
    /// <c>ControlHubCommand.GetFanChannelInfoBytes</c> sends this exact frame
    /// for every channel and reads the per-channel fields out of the single
    /// response (see <see cref="TryParseChannelInfo"/>).
    /// </summary>
    public static byte[] BuildGetInfo() => new byte[] { Frame0, OpControl, SubGetInfo, 0x00 };

    /// <summary>
    /// "Set Fan Speed" request for one PWM port (6 bytes):
    /// <c>FF CC 02 &lt;channel 0..3&gt; &lt;duty%&gt; &lt;enabled&gt;</c>.
    /// Matches <c>ControlHubCommand.SetFanSpeedByChannel</c> /
    /// <c>SetFanPortEnabled</c>. Duty is clamped to 0..100; the channel index
    /// is 0-based on the wire.
    /// </summary>
    public static byte[] BuildSetFanSpeed(int channel, int dutyPercent, bool enabled)
    {
        if (channel < 0 || channel >= FanChannelCount)
            throw new ArgumentOutOfRangeException(nameof(channel), channel, $"Channel must be in 0..{FanChannelCount - 1}.");
        var duty = (byte)Math.Clamp(dutyPercent, FanMinDutyPercent, FanMaxDutyPercent);
        return new byte[] { Frame0, OpControl, SubSetFanSpeed, (byte)channel, duty, (byte)(enabled ? 0x01 : 0x00) };
    }

    /// <summary>
    /// "Set Firmware Animation On/Off" request (4 bytes). The hub runs an
    /// onboard LED animation by default; turning it <b>off</b> hands the ARGB
    /// ports to software streaming (our <see cref="BuildLightingStream"/>
    /// frames). Per <c>ControlHubCommand.SetFwAnimationOnOff</c> the on/off
    /// byte is inverted: <c>0x00</c> = animation ON, <c>0x01</c> = animation OFF.
    /// </summary>
    public static byte[] BuildSetFirmwareAnimation(bool on)
        => new byte[] { Frame0, OpControl, SubSetFwAnimation, (byte)(on ? 0x00 : 0x01) };

    /// <summary>
    /// Build an LED streaming frame for one ARGB port (1..4). Always emits a
    /// fixed-length padded buffer (<see cref="LightingFrameLength"/> = 607
    /// bytes) matching the reference <c>ControlHubController.SendToHardware</c>,
    /// which pads each port's colour list to <see cref="MaxLedsPerPort"/>×3
    /// before writing. Bytes after the 7-byte header are <b>G, R, B</b> per
    /// LED; LEDs past the supplied count stay zero so they go dark.
    /// </summary>
    public static byte[] BuildLightingStream(int port, ReadOnlySpan<RgbColor> leds)
    {
        if (port < 1 || port > ArgbPortCount)
            throw new ArgumentOutOfRangeException(nameof(port), port, $"Port must be in 1..{ArgbPortCount}.");
        var buf = new byte[LightingFrameLength];
        buf[0] = Frame0;
        buf[1] = OpLighting;
        buf[2] = SubStreaming;
        buf[3] = (byte)port;
        // buf[4..6] reserved (0) — header is `FF EE 01 <port> 00 00 00`.
        var count = Math.Min(leds.Length, MaxLedsPerPort);
        for (var i = 0; i < count; i++)
        {
            var off = 7 + i * 3;
            buf[off + 0] = leds[i].G;
            buf[off + 1] = leds[i].R;
            buf[off + 2] = leds[i].B;
        }
        return buf;
    }

    // ── Parsers ──

    /// <summary>Parse the 7-byte firmware-version response into "Major.Minor.Build.Hw". Empty on bad header.</summary>
    public static string ParseFirmwareVersion(ReadOnlySpan<byte> response)
    {
        if (response.Length < FirmwareVersionResponseLength) return "";
        if (response[0] != Frame0 || response[1] != OpVersion || response[2] != SubGetVersion) return "";
        return $"{response[3]}.{response[4]}.{response[5]}.{response[6]}";
    }

    /// <summary>Per-port tach + enabled state decoded from the hub-info response.</summary>
    public readonly record struct SmartHubFanReading(int Rpm, bool Enabled);

    /// <summary>
    /// Parse the 20-byte hub-info response into the four PWM channels'
    /// tach + enabled state. Field layout follows
    /// <c>ControlHubDeviceBase.CheckAndUpdateChannelInfo</c>:
    /// <code>
    ///   ch0: speedH=[3]  speedL=[4]  enabled=[11]
    ///   ch1: speedH=[5]  speedL=[6]  enabled=[12]
    ///   ch2: speedH=[7]  speedL=[8]  enabled=[13]
    ///   ch3: speedH=[9]  speedL=[10] enabled=[14]
    /// </code>
    /// Returns false (and leaves <paramref name="channels"/> null) if the
    /// header isn't the expected <c>FF CC</c> echo or the response is short —
    /// the heartbeat then drops the transport and re-discovers.
    /// </summary>
    public static bool TryParseChannelInfo(ReadOnlySpan<byte> response, out SmartHubFanReading[]? channels)
    {
        channels = null;
        if (response.Length < GetInfoResponseLength) return false;
        if (response[0] != Frame0 || response[1] != OpControl) return false;

        channels = new SmartHubFanReading[FanChannelCount];
        for (var ch = 0; ch < FanChannelCount; ch++)
        {
            var speedH = response[3 + ch * 2];
            var speedL = response[4 + ch * 2];
            var enabled = response[11 + ch] == 0x01;
            channels[ch] = new SmartHubFanReading(DecodeFanRpm(speedH, speedL), enabled);
        }
        return true;
    }

    /// <summary>
    /// Tach decode for the Smart Hub PWM ports. Mirrors
    /// <c>SmartDeviceMethods.GetFanRPM</c> default branch:
    /// <c>rpm = 60000 / (speedH + speedL/100) / 4</c>, and 0 when both bytes
    /// are zero (no tach signal / port empty / fan stopped).
    /// </summary>
    public static int DecodeFanRpm(byte speedH, byte speedL)
    {
        if (speedH == 0x00 && speedL == 0x00) return 0;
        var rpm = (int)(60_000 / (speedH + speedL / 100f)) / 4;
        return rpm > 0 ? rpm : 0;
    }
}

/// <summary>24-bit RGB colour — same wire-level triple as MiniHub / NP50, streamed GRB by <see cref="SmartHubProtocol.BuildLightingStream"/>.</summary>
public readonly record struct RgbColor(byte R, byte G, byte B);
