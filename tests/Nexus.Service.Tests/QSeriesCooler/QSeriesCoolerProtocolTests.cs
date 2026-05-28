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
}
