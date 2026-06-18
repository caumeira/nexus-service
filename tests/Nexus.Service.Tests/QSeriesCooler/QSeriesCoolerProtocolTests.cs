using System;
using Nexus.Service.Peripherals.Hyte.MiniHub;        // RgbColor
using Nexus.Service.Peripherals.Hyte.QSeriesCooler;

namespace Nexus.Service.Tests.QSeriesCooler;

/// <summary>
/// Wire-protocol coverage for the HYTE Q-series cooler RGB path. Reference is
/// HYTE's shipping nexus-control-service —
/// <c>LightDancing/Hardware/Devices/HYTE/Cooler/PQSeriesDeviceBase.SendToHardware</c>:
/// software RGB control (FF DD 03 00) then 4 per-port LED streams
/// <c>FF EE 01 &lt;port&gt; 01 68 00</c> + GRB triples, each PadListWithZeros(90).
/// </summary>
public class QSeriesCoolerProtocolTests
{
    // ── RGB control mode ──

    [Theory]
    [InlineData(QSeriesCoolerProtocol.RgbModeSoftware)]
    [InlineData(QSeriesCoolerProtocol.RgbModeMotherboard)]
    public void BuildSetRgbControlMode_emits_FF_DD_03_mode(byte mode)
    {
        Assert.Equal(new byte[] { 0xFF, 0xDD, 0x03, mode }, QSeriesCoolerProtocol.BuildSetRgbControlMode(mode));
    }

    [Fact]
    public void BuildSetRgbControlMode_rejects_unknown_mode_byte()
    {
        Assert.Throws<ArgumentException>(() => QSeriesCoolerProtocol.BuildSetRgbControlMode(0x99));
    }

    // ── LED streaming framing ──

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void BuildLightingStream_emits_fixed_90_byte_frame_with_header(int port)
    {
        var buf = QSeriesCoolerProtocol.BuildLightingStream(port, new[] { new RgbColor(1, 2, 3) });
        Assert.Equal(QSeriesCoolerProtocol.StreamFrameLength, buf.Length);
        Assert.Equal(90, buf.Length);
        Assert.Equal(0xFF, buf[0]);
        Assert.Equal(0xEE, buf[1]);
        Assert.Equal(0x01, buf[2]);              // Q-series stream sub-op (NOT MiniHub's 0x03)
        Assert.Equal((byte)port, buf[3]);
        Assert.Equal(0x01, buf[4]);              // LED-count magic high
        Assert.Equal(0x68, buf[5]);              // LED-count magic low
        Assert.Equal(0x00, buf[6]);              // reserved
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-1)]
    public void BuildLightingStream_rejects_out_of_range_ports(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            QSeriesCoolerProtocol.BuildLightingStream(port, ReadOnlySpan<RgbColor>.Empty));
    }

    [Fact]
    public void BuildLightingStream_writes_GRB_byte_order_per_LED()
    {
        // GetDisplayColors emits G,R,B (PQSeriesDeviceBase / Q60LCDBacklight).
        var leds = new[]
        {
            new RgbColor(R: 0x11, G: 0x22, B: 0x33),
            new RgbColor(R: 0xAA, G: 0xBB, B: 0xCC),
        };
        var buf = QSeriesCoolerProtocol.BuildLightingStream(port: 2, leds);
        Assert.Equal(0x22, buf[7 + 0]); // G
        Assert.Equal(0x11, buf[7 + 1]); // R
        Assert.Equal(0x33, buf[7 + 2]); // B
        Assert.Equal(0xBB, buf[7 + 3]);
        Assert.Equal(0xAA, buf[7 + 4]);
        Assert.Equal(0xCC, buf[7 + 5]);
    }

    [Fact]
    public void BuildLightingStream_always_emits_LedCount_magic_0x0168()
    {
        foreach (var port in new[] { 1, 2, 3, 4 })
        {
            foreach (var ledCount in new[] { 0, 1, 8, 27, 50 })
            {
                var buf = QSeriesCoolerProtocol.BuildLightingStream(port, new RgbColor[ledCount]);
                Assert.Equal(0x01, buf[4]);
                Assert.Equal(0x68, buf[5]);
                Assert.Equal(90, buf.Length);
            }
        }
    }

    [Fact]
    public void BuildLightingStream_zero_pads_trailing_LEDs()
    {
        var leds = new[] { new RgbColor(0x10, 0x20, 0x30) };
        var buf = QSeriesCoolerProtocol.BuildLightingStream(port: 1, leds);
        Assert.Equal(0x20, buf[7]); // G
        Assert.Equal(0x10, buf[8]); // R
        Assert.Equal(0x30, buf[9]); // B
        for (var i = 10; i < buf.Length; i++)
            Assert.Equal(0x00, buf[i]);
    }

    [Fact]
    public void BuildLightingStream_clamps_to_MaxLedsPerPort_keeping_frame_at_90()
    {
        var tooMany = new RgbColor[200];
        for (var i = 0; i < tooMany.Length; i++) tooMany[i] = new RgbColor(0xFF, 0xFF, 0xFF);
        var buf = QSeriesCoolerProtocol.BuildLightingStream(3, tooMany);
        Assert.Equal(90, buf.Length);
        // All MaxLedsPerPort LEDs written...
        for (var i = 0; i < QSeriesCoolerProtocol.MaxLedsPerPort; i++)
        {
            var off = 7 + i * 3;
            Assert.Equal(0xFF, buf[off + 0]);
            Assert.Equal(0xFF, buf[off + 1]);
            Assert.Equal(0xFF, buf[off + 2]);
        }
        // ...and the trailing pad bytes (90 - 7 - 27*3 = 2) stay zero.
        Assert.Equal(0x00, buf[88]);
        Assert.Equal(0x00, buf[89]);
    }

    [Fact]
    public void MaxLedsPerPort_is_27()
    {
        Assert.Equal(27, QSeriesCoolerProtocol.MaxLedsPerPort);
    }

    // ── Pump telemetry reads ──
    // Reference: SmartHubCommandBase.GetPort0InformationBytes (FF CC 01 00 → 20 B),
    // PQSeriesCommand.GetPump2InfoBytes (FF CC 09 → 7 B), CoolerHubDevice
    // .UpdateOnboardCoolingDeviceInfo (pump tach = data[9],[10]) and
    // SmartDeviceMethods.GetRPM.

    [Fact]
    public void BuildGetPort0Info_emits_FF_CC_01_00()
    {
        Assert.Equal(new byte[] { 0xFF, 0xCC, 0x01, 0x00 }, QSeriesCoolerProtocol.BuildGetPort0Info());
    }

    [Fact]
    public void BuildGetPump2Info_emits_FF_CC_09()
    {
        Assert.Equal(new byte[] { 0xFF, 0xCC, 0x09 }, QSeriesCoolerProtocol.BuildGetPump2Info());
    }

    [Theory]
    [InlineData(0, 0, 0)]       // no-sensor sentinel
    [InlineData(0, 50, 3000)]   // (0*100+50)/10*4 = 20 → 60000/20
    [InlineData(1, 0, 1500)]    // (100)/10*4 = 40 → 60000/40
    public void DecodeRpm_matches_reference_formula(byte high, byte low, int expected)
    {
        Assert.Equal(expected, QSeriesCoolerProtocol.DecodeRpm(high, low));
    }

    [Fact]
    public void TryParsePort0PumpRpm_reads_bytes_9_and_10()
    {
        var resp = new byte[QSeriesCoolerProtocol.Port0ResponseLength];
        resp[0] = 0xFF; resp[1] = 0xCC;
        resp[9] = 1; resp[10] = 0;   // → 1500 RPM
        Assert.True(QSeriesCoolerProtocol.TryParsePort0PumpRpm(resp, out var rpm));
        Assert.Equal(1500, rpm);
    }

    [Theory]
    [InlineData(19)]                 // one byte short
    public void TryParsePort0PumpRpm_rejects_short_response(int length)
    {
        var resp = new byte[length];
        resp[0] = 0xFF; resp[1] = 0xCC;
        Assert.False(QSeriesCoolerProtocol.TryParsePort0PumpRpm(resp, out _));
    }

    [Fact]
    public void TryParsePort0PumpRpm_rejects_mis_echoed_header()
    {
        var resp = new byte[QSeriesCoolerProtocol.Port0ResponseLength];
        resp[0] = 0xFF; resp[1] = 0xDD;   // wrong op byte
        Assert.False(QSeriesCoolerProtocol.TryParsePort0PumpRpm(resp, out _));
    }

    [Fact]
    public void TryParsePump2Rpm_reads_bytes_3_and_4()
    {
        var resp = new byte[QSeriesCoolerProtocol.Pump2ResponseLength];
        resp[0] = 0xFF; resp[1] = 0xCC;
        resp[3] = 0; resp[4] = 50;   // → 3000 RPM
        Assert.True(QSeriesCoolerProtocol.TryParsePump2Rpm(resp, out var rpm));
        Assert.Equal(3000, rpm);
    }

    [Fact]
    public void TryParsePump2Rpm_returns_zero_when_no_second_pump()
    {
        var resp = new byte[QSeriesCoolerProtocol.Pump2ResponseLength];
        resp[0] = 0xFF; resp[1] = 0xCC;   // bytes [3],[4] stay 0
        Assert.True(QSeriesCoolerProtocol.TryParsePump2Rpm(resp, out var rpm));
        Assert.Equal(0, rpm);
    }

    // ── Pump control ──
    // Reference: SmartHubCommandBase.SwitchControlMode / PQSeriesCommand
    // .SetPumpSpeedCommand (15-byte FF CC 02, fw-anim echoed from Port-0 [15..19])
    // and SmartDeviceMethods._pumpSpeedPercentageToVoltagePercentage.

    [Fact]
    public void BuildSetControl_lays_out_mode_speed_turbo_and_echoes_fw_anim()
    {
        var port0 = new byte[QSeriesCoolerProtocol.Port0ResponseLength];
        port0[15] = 0x02; port0[16] = 0xAA; port0[17] = 0xBB; port0[18] = 0xCC; port0[19] = 0x40; // anim, R, G, B, brightness
        var cmd = QSeriesCoolerProtocol.BuildSetControl(
            QSeriesCoolerProtocol.ControlModeSoftware, pumpWire: 35, QSeriesCoolerProtocol.TurboOnByte, port0);
        Assert.Equal(QSeriesCoolerProtocol.SetControlFrameLength, cmd.Length);
        Assert.Equal(new byte[] { 0xFF, 0xCC, 0x02 }, cmd[0..3]);
        Assert.Equal(QSeriesCoolerProtocol.ControlModeSoftware, cmd[4]);
        Assert.Equal(35, cmd[5]);
        Assert.Equal(QSeriesCoolerProtocol.TurboOnByte, cmd[9]);
        // fw-animation echoed from Port-0 [15..19] into command [10..14]
        Assert.Equal(new byte[] { 0x02, 0xAA, 0xBB, 0xCC, 0x40 }, cmd[10..15]);
    }

    [Fact]
    public void BuildSetTurboMcu_emits_FF_CC_0A_turbo()
    {
        Assert.Equal(new byte[] { 0xFF, 0xCC, 0x0A, 0x01 }, QSeriesCoolerProtocol.BuildSetTurboMcu(0x01));
    }

    [Theory]
    [InlineData(45, true, 0)]    // pump off below 46%
    [InlineData(46, true, 31)]
    [InlineData(50, true, 35)]
    [InlineData(100, true, 100)]
    public void MapPumpDutyToWire_turbo_follows_voltage_table(int duty, bool turbo, int expected)
    {
        Assert.Equal(expected, QSeriesCoolerProtocol.MapPumpDutyToWire(duty, turbo));
    }

    [Theory]
    [InlineData(50, 35)]   // <=55 maps via the table
    [InlineData(80, 55)]   // off-turbo the wire byte caps at 55
    public void MapPumpDutyToWire_offTurbo_caps_at_55(int duty, int expected)
    {
        Assert.Equal(expected, QSeriesCoolerProtocol.MapPumpDutyToWire(duty, turboOn: false));
    }

    // ── Firmware temperature curve (FF CC 03 / FF CC 04) ──

    [Theory]
    [InlineData("q60", "2.0.0.1", true)]    // exactly the Q60 threshold
    [InlineData("q60", "2.0.9.1", true)]
    [InlineData("q60", "1.9.9.9", false)]   // below threshold
    [InlineData("q80", "1.0.4.1", true)]    // exactly the Q80 threshold
    [InlineData("q80", "1.0.3.9", false)]
    [InlineData("q60", "", false)]          // no version polled yet
    public void SupportsFirmwareCurve_gates_on_version(string variant, string version, bool expected)
    {
        Assert.Equal(expected, QSeriesCoolerProtocol.SupportsFirmwareCurve(variant, version));
    }

    [Fact]
    public void BuildGetFirmwareDefault_emits_FF_CC_04_00()
    {
        Assert.Equal(new byte[] { 0xFF, 0xCC, 0x04, 0x00 }, QSeriesCoolerProtocol.BuildGetFirmwareDefault());
    }

    [Theory]
    [InlineData(false)]   // pump thermistor table
    [InlineData(true)]    // fan thermistor table
    public void CurveTemp_round_trips_for_every_editor_temperature(bool fan)
    {
        for (var t = QSeriesCoolerProtocol.FirmwareCurveTempMin; t <= QSeriesCoolerProtocol.FirmwareCurveTempMax; t++)
        {
            var (high, low) = QSeriesCoolerProtocol.EncodeCurveTemp(t, fan);
            Assert.Equal(t, QSeriesCoolerProtocol.DecodeCurveTemp(high, low, fan));
        }
    }

    [Fact]
    public void BuildSetFirmwareMode_lays_out_header_mode_speeds_and_save()
    {
        var pts = new QSeriesFirmwareCurvePoint[]
        {
            new() { PumpTempC = 0,  PumpDutyPercent = 50, FanTempC = 0,  FanDutyPercent = 35 },
            new() { PumpTempC = 20, PumpDutyPercent = 60, FanTempC = 20, FanDutyPercent = 45 },
            new() { PumpTempC = 30, PumpDutyPercent = 70, FanTempC = 30, FanDutyPercent = 55 },
            new() { PumpTempC = 40, PumpDutyPercent = 85, FanTempC = 40, FanDutyPercent = 75 },
            new() { PumpTempC = 50, PumpDutyPercent = 100, FanTempC = 50, FanDutyPercent = 100 },
        };
        var cmd = QSeriesCoolerProtocol.BuildSetFirmwareMode(QSeriesCoolerProtocol.FwDefaultModeTemperature, pts);

        Assert.Equal(QSeriesCoolerProtocol.SetFirmwareModeFrameLength, cmd.Length);
        Assert.Equal(new byte[] { 0xFF, 0xCC, 0x03, 0x00 }, cmd[0..4]);
        Assert.Equal(QSeriesCoolerProtocol.FwDefaultModeTemperature, cmd[4]);
        // Pump speeds: slots 0-2 at [5..7], slots 3-4 at [17..18].
        Assert.Equal(new byte[] { 50, 60, 70 }, cmd[5..8]);
        Assert.Equal(new byte[] { 85, 100 }, cmd[17..19]);
        // Fan speeds: slots 0-2 at [8..10], slots 3-4 at [19..20].
        Assert.Equal(new byte[] { 35, 45, 55 }, cmd[8..11]);
        Assert.Equal(new byte[] { 75, 100 }, cmd[19..21]);
        // SAVE byte commits to EEPROM.
        Assert.Equal(0x01, cmd[35]);
    }

    [Fact]
    public void BuildSetFirmwareMode_rejects_wrong_point_count()
    {
        var three = new QSeriesFirmwareCurvePoint[3];
        Assert.Throws<ArgumentException>(() =>
            QSeriesCoolerProtocol.BuildSetFirmwareMode(QSeriesCoolerProtocol.FwDefaultModeTemperature, three));
    }

    [Fact]
    public void TryParseFirmwareCurve_round_trips_a_built_frame()
    {
        var pts = new QSeriesFirmwareCurvePoint[]
        {
            new() { PumpTempC = 5,  PumpDutyPercent = 50, FanTempC = 10, FanDutyPercent = 35 },
            new() { PumpTempC = 18, PumpDutyPercent = 62, FanTempC = 22, FanDutyPercent = 48 },
            new() { PumpTempC = 30, PumpDutyPercent = 75, FanTempC = 33, FanDutyPercent = 60 },
            new() { PumpTempC = 41, PumpDutyPercent = 88, FanTempC = 44, FanDutyPercent = 80 },
            new() { PumpTempC = 50, PumpDutyPercent = 100, FanTempC = 50, FanDutyPercent = 95 },
        };
        var cmd = QSeriesCoolerProtocol.BuildSetFirmwareMode(QSeriesCoolerProtocol.FwDefaultModeMix, pts);

        // FF CC 04 response carries the same [4..34] layout (no SAVE byte).
        Assert.True(QSeriesCoolerProtocol.TryParseFirmwareCurve(cmd.AsSpan(0, QSeriesCoolerProtocol.FirmwareDefaultResponseLength), out var parsed));
        Assert.Equal(QSeriesCoolerProtocol.FwDefaultModeMix, QSeriesCoolerProtocol.FirmwareDefaultModeOf(cmd));
        for (var i = 0; i < pts.Length; i++)
        {
            Assert.Equal(pts[i].PumpDutyPercent, parsed[i].PumpDutyPercent);
            Assert.Equal(pts[i].FanDutyPercent, parsed[i].FanDutyPercent);
            Assert.Equal(pts[i].PumpTempC, parsed[i].PumpTempC);
            Assert.Equal(pts[i].FanTempC, parsed[i].FanTempC);
        }
    }

    [Fact]
    public void TryParseFirmwareCurve_rejects_short_or_misframed()
    {
        Assert.False(QSeriesCoolerProtocol.TryParseFirmwareCurve(new byte[10], out _));
        var wrongHeader = new byte[QSeriesCoolerProtocol.FirmwareDefaultResponseLength];
        wrongHeader[0] = 0xFF; wrongHeader[1] = 0xDD;
        Assert.False(QSeriesCoolerProtocol.TryParseFirmwareCurve(wrongHeader, out _));
    }
}
