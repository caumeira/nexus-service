using System.Collections.Generic;
using Nexus.Service.Models.Sensors;

namespace Nexus.Service.Sensors;

/// <summary>
/// Synthesizes aggregate CPU clock sensors that LHM does not provide natively.
/// The logic lives in its own file so it compiles and tests on all platforms
/// even though <see cref="LibreHardwareSensorProvider"/> is Windows-only.
/// </summary>
internal static class CpuClockAggregates
{
    // Per-core clocks: Type=="Clock", Name contains "Core" (matches P-Core/E-Core/
    // Core variants, excludes Bus Speed and Memory which lack "Core" in their names).
    internal static void Append(
        string hwId, string hwName, List<HardwareSensor> mapped, List<HardwareSensor> result)
    {
        var max = 0f;
        var sum = 0f;
        var count = 0;
        for (var i = 0; i < mapped.Count; i++)
        {
            var s = mapped[i];
            if (!string.Equals(s.Type, "Clock", StringComparison.OrdinalIgnoreCase)) continue;
            if (!s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)) continue;
            if (s.Value > max) max = s.Value;
            sum += s.Value;
            count++;
        }
        if (count == 0) return;

        var avg = sum / count;
        var parent = new SensorParent { Id = hwId, Name = hwName };
        result.Add(new HardwareSensor
        {
            Id = $"{hwId}/clock/core-max",
            Name = "Core Max",
            Type = "Clock",
            Units = "MHz",
            Value = max,
            Formatted = $"{max:F0} MHz",
            Parent = parent,
        });
        result.Add(new HardwareSensor
        {
            Id = $"{hwId}/clock/core-average",
            Name = "Core Average",
            Type = "Clock",
            Units = "MHz",
            Value = avg,
            Formatted = $"{avg:F0} MHz",
            Parent = parent,
        });
    }
}
