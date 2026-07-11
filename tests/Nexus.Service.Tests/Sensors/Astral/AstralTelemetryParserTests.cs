using Nexus.Service.Sensors.Astral;

namespace Nexus.Service.Tests.Sensors.Astral;

public class AstralTelemetryParserTests
{
    // Layout independently derived from LibreHardwareMonitor NvidiaGpu.cs
    // TryReadAstral12VHPwrPinSensors and cross-checked against
    // Timic3/astral-power-monitoring's register map: big-endian words, pins
    // in descending order (voltage before current per pin).
    private static byte[] BuildBlock((float voltageVolts, float currentAmps)[] pins)
    {
        var block = new byte[AstralTelemetryParser.BlockSize];
        for (var pin = 1; pin <= AstralTelemetryParser.PinCount; pin++)
        {
            var voltageWord = 12 - 2 * pin;
            var currentWord = voltageWord + 1;
            WriteWord(block, voltageWord, (ushort)MathF.Round(pins[pin - 1].voltageVolts * 1000));
            WriteWord(block, currentWord, (ushort)MathF.Round(pins[pin - 1].currentAmps * 1000));
        }
        return block;
    }

    private static void WriteWord(byte[] block, int wordIndex, ushort value)
    {
        block[wordIndex * 2] = (byte)(value >> 8);
        block[wordIndex * 2 + 1] = (byte)(value & 0xFF);
    }

    [Fact]
    public void Parse_maps_each_pin_to_its_own_voltage_and_current()
    {
        var pins = new (float, float)[]
        {
            (12.000f, 5.000f),
            (12.100f, 5.100f),
            (12.200f, 5.200f),
            (12.300f, 5.300f),
            (12.400f, 5.400f),
            (12.500f, 5.500f),
        };
        var block = BuildBlock(pins);

        var readout = AstralTelemetryParser.Parse(block);

        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(pins[i].Item1, readout.Pins[i].VoltageVolts, 3);
            Assert.Equal(pins[i].Item2, readout.Pins[i].CurrentAmps, 3);
            Assert.Equal(pins[i].Item1 * pins[i].Item2, readout.Pins[i].PowerWatts, 2);
        }
    }

    [Fact]
    public void Parse_sums_connector_current_and_power_across_all_pins()
    {
        var pins = new (float, float)[]
        {
            (12.000f, 5.000f),
            (12.100f, 5.100f),
            (12.200f, 5.200f),
            (12.300f, 5.300f),
            (12.400f, 5.400f),
            (12.500f, 5.500f),
        };
        var block = BuildBlock(pins);

        var readout = AstralTelemetryParser.Parse(block);

        var expectedCurrent = pins.Sum(p => p.Item2);
        var expectedPower = pins.Sum(p => p.Item1 * p.Item2);

        Assert.Equal(expectedCurrent, readout.ConnectorCurrentAmps, 2);
        Assert.Equal(expectedPower, readout.ConnectorPowerWatts, 1);
    }

    [Fact]
    public void Parse_does_not_transpose_adjacent_pins()
    {
        // Every pin gets a distinct value so a word-index off-by-one would
        // fail this assertion instead of silently averaging out.
        var pins = new (float, float)[]
        {
            (11.000f, 1.000f),
            (11.100f, 2.000f),
            (11.200f, 3.000f),
            (11.300f, 4.000f),
            (11.400f, 5.000f),
            (11.500f, 6.000f),
        };
        var block = BuildBlock(pins);

        var readout = AstralTelemetryParser.Parse(block);

        Assert.Equal(1.000f, readout.Pins[0].CurrentAmps, 3);
        Assert.Equal(6.000f, readout.Pins[5].CurrentAmps, 3);
        Assert.Equal(11.000f, readout.Pins[0].VoltageVolts, 3);
        Assert.Equal(11.500f, readout.Pins[5].VoltageVolts, 3);
    }

    [Fact]
    public void Parse_throws_on_wrong_block_size()
    {
        Assert.Throws<ArgumentException>(() => AstralTelemetryParser.Parse(new byte[10]));
    }
}
