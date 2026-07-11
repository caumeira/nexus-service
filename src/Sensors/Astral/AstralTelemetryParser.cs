using System;

namespace Nexus.Service.Sensors.Astral;

/// <summary>
/// Parses the block read from an ASUS ROG Astral GPU's onboard ITE IT8915FN
/// power-monitor IC into per-pin 12VHPWR voltage/current/power plus
/// connector totals.
///
/// Byte layout: big-endian words holding millivolts/milliamps, pins in
/// descending order (voltage before current per pin). Mirrors
/// LibreHardwareMonitor's Hardware/Gpu/NvidiaGpu.cs
/// TryReadAstral12VHPwrPinSensors and its per-pin Update loop, independently
/// confirmed by Timic3/astral-power-monitoring's README register map (same
/// IC, same reversed rail order).
/// </summary>
internal static class AstralTelemetryParser
{
    public const int BlockSize = 24;
    public const int PinCount = 6;

    // All six 12VHPWR pins sit on the same 12V rail, so a genuine read has every
    // pin near 12V. A read from a non-Astral card that happens to answer at 0x56
    // (or a corrupt transfer) won't - this band rejects it so we don't emit bogus
    // amps or mislabel the card "ROG Astral". Deliberately wide (rail sag/spike).
    private const float MinPlausiblePinVolts = 6f;
    private const float MaxPlausiblePinVolts = 16f;

    public readonly record struct PinReading(float VoltageVolts, float CurrentAmps, float PowerWatts);

    public readonly record struct AstralReadout(
        PinReading[] Pins,
        float ConnectorCurrentAmps,
        float ConnectorPowerWatts);

    public static AstralReadout Parse(ReadOnlySpan<byte> block)
    {
        if (block.Length != BlockSize)
        {
            throw new ArgumentException(
                $"Astral telemetry block must be {BlockSize} bytes, got {block.Length}.", nameof(block));
        }

        var wordCount = PinCount * 2;
        var words = new ushort[wordCount];
        for (var i = 0; i < wordCount; i++)
        {
            var wordIndex = wordCount - 1 - i;
            words[i] = (ushort)((block[wordIndex * 2] << 8) | block[wordIndex * 2 + 1]);
        }

        var pins = new PinReading[PinCount];
        var connectorCurrent = 0f;
        var connectorPower = 0f;
        for (var i = 0; i < PinCount; i++)
        {
            var current = words[i * 2] / 1000f;
            var voltage = words[i * 2 + 1] / 1000f;
            if (voltage < MinPlausiblePinVolts || voltage > MaxPlausiblePinVolts)
            {
                throw new ArgumentException(
                    $"Astral pin {i + 1} voltage {voltage:F3}V outside the plausible 12V-rail band; " +
                    "not an Astral power-monitor block.", nameof(block));
            }
            var power = voltage * current;
            pins[i] = new PinReading(voltage, current, power);
            connectorCurrent += current;
            connectorPower += power;
        }

        return new AstralReadout(pins, connectorCurrent, connectorPower);
    }
}
