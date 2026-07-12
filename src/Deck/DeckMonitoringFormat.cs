using System;
using Nexus.Service.Models.Sensors;

namespace Nexus.Service.Deck;

/// <summary>
/// Ports nexus-web's deck monitoring tile text rules (MonitoringWidget.tsx's
/// labelForDevice/sensorNames.ts and sensorValueFormat.ts's
/// formatScaledDataValue) so the physical key matches the touch-panel
/// DeckMonitoringCell for the same sensor. Category strings are the deck
/// monitoring v1 set: quick, cpu, gpu, memory, motherboard, storage.
/// </summary>
internal static class DeckMonitoringFormat
{
    private static readonly string[] DataUnitLadder = { "B", "KB", "MB", "GB", "TB", "PB" };

    /// <summary>Categories whose sensor names arrive with the category baked in ("CPU Total"), mirroring sensorNames.ts's DEVICE_PREFIXES.</summary>
    private static string? DevicePrefix(string? category) => category switch
    {
        "cpu" => "CPU",
        "gpu" => "GPU",
        "memory" => "Memory",
        _ => null,
    };

    /// <summary>
    /// Default top label for a monitoring tile when the slot has no
    /// LabelText override, mirroring labelForDevice: "&lt;Prefix&gt; &lt;Sensor&gt;"
    /// for a prefixed category (normalizing an already-prefixed or bare
    /// sensor name to the same output), the sensor name unchanged for any
    /// other category, or the category's own display name when the sensor
    /// id is unresolved.
    /// </summary>
    internal static string ResolveLabel(string? category, string sensorName)
    {
        if (!string.IsNullOrEmpty(sensorName))
        {
            var prefix = DevicePrefix(category);
            if (prefix is null)
            {
                return sensorName;
            }
            var bare = StripPrefix(sensorName, prefix);
            return bare.Length > 0 ? $"{prefix} {bare}" : prefix;
        }
        return category switch
        {
            "quick" => "Quick",
            "cpu" => "CPU",
            "gpu" => "GPU",
            "memory" => "RAM",
            "motherboard" => "MB",
            "storage" => "Storage",
            _ => "",
        };
    }

    /// <summary>Mirrors bareSensorLabel: strips a leading "&lt;prefix&gt; " (case-insensitive), or returns "" for a name equal to the prefix alone.</summary>
    private static string StripPrefix(string name, string prefix)
    {
        if (string.Equals(name, prefix, StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }
        if (name.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase))
        {
            return name[(prefix.Length + 1)..];
        }
        return name;
    }

    /// <summary>
    /// Value text for a resolved sensor: auto-scales a byte-based Units
    /// reading along the B/KB/MB/GB/TB/PB ladder (mirroring
    /// formatScaledDataValue), falling back to the service's own formatted
    /// string for any unit outside that ladder (percent, temperature, clock,
    /// RPM, ...).
    /// </summary>
    internal static string ResolveValueText(HardwareSensor sensor) =>
        FormatScaledDataValue(sensor.Value, sensor.Units) ?? sensor.Formatted;

    private static string? FormatScaledDataValue(float value, string units)
    {
        if (!float.IsFinite(value))
        {
            return null;
        }
        var unitIndex = Array.IndexOf(DataUnitLadder, (units ?? "").ToUpperInvariant());
        if (unitIndex < 0)
        {
            return null;
        }

        var scaled = Math.Max(0f, value);
        var index = unitIndex;
        while (scaled >= 1024f && index < DataUnitLadder.Length - 1)
        {
            scaled /= 1024f;
            index++;
        }
        while (scaled > 0f && scaled < 1f && index > 0)
        {
            scaled *= 1024f;
            index--;
        }

        return scaled == MathF.Floor(scaled)
            ? $"{scaled:F0} {DataUnitLadder[index]}"
            : $"{scaled:F1} {DataUnitLadder[index]}";
    }
}
