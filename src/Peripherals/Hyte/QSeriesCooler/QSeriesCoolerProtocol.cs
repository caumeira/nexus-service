using System;
using System.Text;
using Nexus.Service.Peripherals.Hyte.MiniHub; // RgbColor: shared HYTE serial RGB triple

namespace Nexus.Service.Peripherals.Hyte.QSeriesCooler;

/// <summary>
/// Pure builders + parsers for the HYTE Q-series (Q60 / Q80 "THICC" AIO)
/// cooler-controller serial protocol — the STM32 controller that the bundled
/// <c>q60/*.hex</c> / <c>q80/*.hex</c> images flash. This is NOT the Android
/// LCD panel (that's the ADB-based <c>src/QSeries/</c> stack); the cooler
/// controller enumerates as a separate USB-CDC virtual COM port
/// (VID 3402, PID 0400 = Q60, PID 0403 = Q80).
///
/// Ported from HYTE's nexus-control-service <c>SmartHubCommandBase</c>. The
/// Q-series shares the "smart hub" command family with the MiniHub, so the
/// firmware-version exchange is byte-identical to
/// <c>MiniHubProtocol.BuildGetFirmwareVersion</c> (0xFF 0xDD 0x02 → 7 bytes,
/// version = bytes [3..6]).
/// </summary>
public static class QSeriesCoolerProtocol
{
    // ── USB identity ──

    public const int VendorId = 0x3402;
    public const int Q60ProductId = 0x0400;
    public const int Q80ProductId = 0x0403;

    /// <summary>Firmware-catalog keys (bundled-.hex directory names).</summary>
    public const string VariantQ60 = "q60";
    public const string VariantQ80 = "q80";

    /// <summary>Operating USB PID for a variant key (for the OTA product key), or -1 if unknown.</summary>
    public static int ProductIdForVariant(string variant) => variant switch
    {
        VariantQ60 => Q60ProductId,
        VariantQ80 => Q80ProductId,
        _ => -1,
    };

    // ── Wire constants ──

    private const byte Frame0 = 0xFF;
    private const byte OpControl = 0xDD;        // version / mode / fan / get
    private const byte OpCooler = 0xCC;         // cooler status/control: pump/fan info, mode, turbo, warnings
    private const byte OpSerialA = 0xAA;        // serial-number query prefix
    private const byte OpSerialB = 0xBB;
    private const byte SubGetFirmwareVersion = 0x02;
    private const byte SubGetSerial = 0x02;
    private const byte SubGetInfo = 0x01;       // port-0 status (port byte 0) / per-channel smart-device info
    private const byte SubGetPump2 = 0x09;      // Q80 second-pump RPM
    private const byte SubSetControl = 0x02;    // set pump speed + control mode + turbo (15-byte frame)
    private const byte SubSetTurboMcu = 0x0A;   // persist turbo state to the MCU
    private const byte Port0 = 0x00;

    /// <summary>Hub control mode (Port-0 byte [12]; SetControl byte [4]).</summary>
    public const byte ControlModeKeep = 0x00;        // [4]=0: apply speed without re-asserting mode (HYTE SetPumpSpeedCommand)
    public const byte ControlModeSoftware = 0x01;    // host drives the pump
    public const byte ControlModeMotherboard = 0x02; // motherboard PWM drives the pump (power-on default)
    public const byte ControlModeFirmware = 0x03;    // onboard temperature curve drives the pump
    public const byte ControlModeMix = 0x04;

    /// <summary>Turbo byte convention shared by Port-0 [14] and SetControl [9]: 0x00 = on, 0x01 = off.</summary>
    public const byte TurboOnByte = 0x00;
    public const byte TurboOffByte = 0x01;

    public const int SetControlFrameLength = 15;

    public const int FirmwareVersionResponseLength = 7;
    public const int SerialResponseLength = 37;

    /// <summary>Length of the Port-0 status response (pump tach in bytes [9..10]).</summary>
    public const int Port0ResponseLength = 20;

    /// <summary>Length of the Q80 second-pump response (tach in bytes [3..4]).</summary>
    public const int Pump2ResponseLength = 7;

    // ── Lighting wire constants ──
    //
    // Mirrors the legacy PQSeriesDeviceBase.SendToHardware flow: enable software
    // RGB control, then stream each of the 4 LED ports as a fixed 90-byte frame.
    // Bytes after the 7-byte header are G,R,B triples (HYTE firmware expects GRB,
    // same as MiniHub/NP50). The two LED-count header bytes are the hardcoded
    // magic 0x01 0x68 the reference agent always emits regardless of real count.
    private const byte OpLighting = 0xEE;          // LED streaming
    private const byte SubStreaming = 0x01;        // Q-series stream sub-op (MiniHub uses 0x03)
    private const byte SubSetRgbControlMode = 0x03;
    private const byte LedCountMagicHigh = 0x01;
    private const byte LedCountMagicLow = 0x68;

    public const byte RgbModeSoftware = 0x00;      // nexus drives the LEDs
    public const byte RgbModeMotherboard = 0x01;   // hand off to the mobo ARGB header (power-on default)

    /// <summary>The Q-series cooler hub streams over 4 LED ports (pump head + fan/strip channels).</summary>
    public const int LedPortCount = 4;

    /// <summary>Every streamed port frame is exactly this many bytes: 7-byte header + zero-padded GRB data.</summary>
    public const int StreamFrameLength = 90;

    /// <summary>Max LEDs carried in one port frame = floor((90 - 7) / 3).</summary>
    public const int MaxLedsPerPort = (StreamFrameLength - 7) / 3; // 27

    // ── Builders ──

    /// <summary>Build the "Get Firmware Version" request (3 bytes).</summary>
    public static byte[] BuildGetFirmwareVersion() => new byte[] { Frame0, OpControl, SubGetFirmwareVersion };

    /// <summary>Build the "Get Serial Number" request (4 bytes).</summary>
    public static byte[] BuildGetSerial() => new byte[] { Frame0, OpSerialA, OpSerialB, SubGetSerial };

    /// <summary>Build the "Get Port-0 Information" request (4 bytes). Response carries pump tach + temps + hub mode.</summary>
    public static byte[] BuildGetPort0Info() => new byte[] { Frame0, OpCooler, SubGetInfo, Port0 };

    /// <summary>Build the Q80 second-pump info request (3 bytes).</summary>
    public static byte[] BuildGetPump2Info() => new byte[] { Frame0, OpCooler, SubGetPump2 };

    /// <summary>
    /// Build the "Set RGB Control Mode" request (4 bytes). <see cref="RgbModeSoftware"/> gives
    /// nexus full control of the LEDs; <see cref="RgbModeMotherboard"/> hands off to the
    /// motherboard ARGB header (the default after a power cycle). Software mode must be set
    /// before any <see cref="BuildLightingStream"/> write actually reaches the LEDs.
    /// </summary>
    public static byte[] BuildSetRgbControlMode(byte mode)
    {
        if (mode != RgbModeSoftware && mode != RgbModeMotherboard)
            throw new ArgumentException($"Unknown RGB control mode 0x{mode:X2}", nameof(mode));
        return new byte[] { Frame0, OpControl, SubSetRgbControlMode, mode };
    }

    /// <summary>
    /// Build a fixed 90-byte LED streaming frame for one port (1..<see cref="LedPortCount"/>).
    /// Header is <c>FF EE 01 &lt;port&gt; 01 68 00</c>; the remaining bytes are G,R,B triples,
    /// zero-padded past the supplied LED count so trailing/disconnected LEDs go dark. LEDs past
    /// <see cref="MaxLedsPerPort"/> are dropped to keep the frame exactly <see cref="StreamFrameLength"/>.
    /// Matches legacy PQSeriesDeviceBase.SendToHardware (4 ports, PadListWithZeros(90), GRB order).
    /// </summary>
    public static byte[] BuildLightingStream(int port, ReadOnlySpan<RgbColor> leds)
    {
        if (port < 1 || port > LedPortCount)
            throw new ArgumentOutOfRangeException(nameof(port), port, $"Port must be in 1..{LedPortCount}.");
        var buf = new byte[StreamFrameLength];
        buf[0] = Frame0;
        buf[1] = OpLighting;
        buf[2] = SubStreaming;
        buf[3] = (byte)port;
        buf[4] = LedCountMagicHigh;
        buf[5] = LedCountMagicLow;
        // buf[6] reserved (0)
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

    /// <summary>
    /// Parse the 7-byte firmware-version response into "Major.Minor.Build.Hw"
    /// (e.g. "2.0.9.1"). Returns empty on a short or mis-echoed reply. The
    /// firmware echoes the command header (0xFF 0xDD) in bytes [0..1].
    /// </summary>
    public static string ParseFirmwareVersion(ReadOnlySpan<byte> response)
    {
        if (response.Length < FirmwareVersionResponseLength) return "";
        if (response[0] != Frame0 || response[1] != OpControl) return "";
        return $"{response[3]}.{response[4]}.{response[5]}.{response[6]}";
    }

    /// <summary>
    /// Parse the 37-byte serial-number response. The serial is a UTF-8 string
    /// in bytes [4..30); the legacy treats 0xFF in the trailing bytes as
    /// "unset" and returns empty.
    /// </summary>
    public static string ParseSerial(ReadOnlySpan<byte> response)
    {
        if (response.Length < SerialResponseLength) return "";
        if (response[0] != Frame0 || response[1] != OpSerialA) return "";
        if (response[30] == 0xFF && response[31] == 0xFF) return "";
        return Encoding.UTF8.GetString(response.Slice(4, 26)).TrimEnd('\0');
    }

    /// <summary>
    /// Decode a HYTE 2-byte tach reading to RPM. Both bytes zero is the
    /// firmware's "no sensor" sentinel → 0. Matches SmartDeviceMethods.GetRPM.
    /// </summary>
    public static int DecodeRpm(byte high, byte low)
    {
        if (high == 0x00 && low == 0x00) return 0;
        return (int)(60 * 1000 / ((high * 100 + (float)low) / 10 * 4));
    }

    /// <summary>
    /// Parse the pump RPM from the 20-byte Port-0 status response (tach in
    /// bytes [9..10]). Returns false on a short or mis-echoed reply.
    /// </summary>
    public static bool TryParsePort0PumpRpm(ReadOnlySpan<byte> response, out int pumpRpm)
    {
        pumpRpm = 0;
        if (response.Length < Port0ResponseLength) return false;
        if (response[0] != Frame0 || response[1] != OpCooler) return false;
        pumpRpm = DecodeRpm(response[9], response[10]);
        return true;
    }

    /// <summary>
    /// Parse the Q80 second-pump RPM from the 7-byte response (tach in bytes
    /// [3..4]). Returns false on a short or mis-echoed reply; pumpRpm is 0 when
    /// no second pump is present.
    /// </summary>
    public static bool TryParsePump2Rpm(ReadOnlySpan<byte> response, out int pumpRpm)
    {
        pumpRpm = 0;
        if (response.Length < Pump2ResponseLength) return false;
        if (response[0] != Frame0 || response[1] != OpCooler) return false;
        pumpRpm = DecodeRpm(response[3], response[4]);
        return true;
    }

    /// <summary>The hub control mode the last Port-0 poll reported (byte [12]).</summary>
    public static byte ControlModeOf(ReadOnlySpan<byte> port0) =>
        port0.Length >= Port0ResponseLength ? port0[12] : ControlModeMotherboard;

    /// <summary>True when the last Port-0 poll reports turbo on (byte [14] == 0x00).</summary>
    public static bool TurboOnOf(ReadOnlySpan<byte> port0) =>
        port0.Length >= Port0ResponseLength && port0[14] == TurboOnByte;

    // ── Control builders ──

    /// <summary>
    /// Build the 15-byte cooler control frame (FF CC 02). Sets control mode [4],
    /// pump speed [5] and turbo [9], echoing the firmware-animation state
    /// (anim mode + RGB + brightness) from the Port-0 response bytes [15..19] so a
    /// control write never clobbers the onboard LED state. Mirrors
    /// SmartHubCommandBase.SwitchControlMode / PQSeriesCommand.SetPumpSpeedCommand.
    /// </summary>
    public static byte[] BuildSetControl(byte mode, byte pumpWire, byte turboByte, ReadOnlySpan<byte> port0)
    {
        var cmd = new byte[SetControlFrameLength];
        cmd[0] = Frame0;
        cmd[1] = OpCooler;
        cmd[2] = SubSetControl;
        cmd[4] = mode;
        cmd[5] = pumpWire;
        cmd[9] = turboByte;
        if (port0.Length >= Port0ResponseLength)
        {
            cmd[10] = port0[15];
            cmd[11] = port0[16];
            cmd[12] = port0[17];
            cmd[13] = port0[18];
            cmd[14] = port0[19];
        }
        return cmd;
    }

    /// <summary>Build the turbo-persist MCU write (FF CC 0A &lt;turbo&gt;).</summary>
    public static byte[] BuildSetTurboMcu(byte turboByte) => new byte[] { Frame0, OpCooler, SubSetTurboMcu, turboByte };

    /// <summary>
    /// Map a 0-100 pump duty to the firmware's voltage-percentage wire byte.
    /// The pump is off below 46% and ramps non-linearly above; off-turbo the
    /// wire byte is additionally capped at 55. Matches PQSeriesCommand
    /// .SetPumpSpeedCommand over SmartDeviceMethods._pumpSpeedPercentageToVoltagePercentage.
    /// </summary>
    public static byte MapPumpDutyToWire(int dutyPercent, bool turboOn)
    {
        var d = Math.Clamp(dutyPercent, 0, 100);
        if (turboOn) return PumpDutyToWire[d];
        return d > 55 ? (byte)55 : PumpDutyToWire[d];
    }

    // Index = pump duty %, value = firmware voltage-% wire byte. Zero below 46%
    // (the pump's minimum). HYTE _pumpSpeedPercentageToVoltagePercentage.
    private static readonly byte[] PumpDutyToWire =
    {
        0,0,0,0,0,0,0,0,0,0, 0,0,0,0,0,0,0,0,0,0,                 //  0-19
        0,0,0,0,0,0,0,0,0,0, 0,0,0,0,0,0,0,0,0,0,                 // 20-39
        0,0,0,0,0,0,                                             // 40-45
        31,32,33,34,35,36,36,37,38,39,                          // 46-55
        40,41,42,45,45,45,47,48,49,50,                          // 56-65
        51,52,54,55,56,58,59,60,61,61,                          // 66-75
        62,63,65,65,69,71,72,74,75,76,                          // 76-85
        77,78,80,80,81,85,87,88,92,94,                          // 86-95
        95,95,99,99,100,                                        // 96-100
    };
}
