using Nexus.Service.Sensors;
using LibreHardwareMonitor.Hardware;

namespace Nexus.Service.Platform.Windows;

/// <summary>
/// Windows system-wide CPU + memory sampler. Reuses the shared
/// <see cref="LhmComputer"/> that the sensor + fan-control providers already
/// keep warm, so the monitoring composite frame uses the exact same numbers
/// the CPU / Memory sub-tabs show. No duplicate reading path, no extra
/// hardware polling -- just pulls the already-cached sensor values.
///
/// - CPU: CPU hardware's "CPU Total" Load sensor (LHM's aggregated usage).
/// - Memory: Memory hardware's Load sensor (used-percent).
/// </summary>
public sealed class WindowsPerformanceProvider : IPerformanceProvider
{
    private readonly LhmComputer _lhm;

    public WindowsPerformanceProvider(LhmComputer lhm)
    {
        _lhm = lhm;
    }

    public Task<PerformanceSnapshot> SampleAsync(CancellationToken ct = default)
    {
        // 100 ms floor dedupes with whichever caller refreshed the hardware
        // tree most recently (ProcessMonitor, sensor route, cooling tick).
        _lhm.Update();
        return Task.FromResult(new PerformanceSnapshot
        {
            Cpu = ReadCpuTotal(),
            Memory = ReadMemoryLoad(),
            Gpu = null,
            Source = "Windows:lhm",
        });
    }

    private double? ReadCpuTotal()
    {
        // Plain foreach: called at monitoring broadcast cadence (~1 Hz). Not
        // a hot path but there's no reason to allocate a Linq enumerator.
        foreach (var hw in _lhm.Instance.Hardware)
        {
            if (hw.HardwareType != HardwareType.Cpu) continue;
            // LHM exposes the aggregated usage as "CPU Total" (Load).
            // Per-core Load sensors share the same SensorType so we filter by
            // name to avoid picking up a single core's value.
            foreach (var s in hw.Sensors)
            {
                if (s.SensorType != SensorType.Load) continue;
                if (!s.Name.Equals("CPU Total", System.StringComparison.OrdinalIgnoreCase)) continue;
                if (s.Value is float v) return System.Math.Clamp((double)v, 0, 100);
            }
        }
        return null;
    }

    private double? ReadMemoryLoad()
    {
        // Prefer the physical RAM bank ("/ram"); fall back to the first memory
        // Load sensor we find (some SKUs report different identifiers).
        double? fallback = null;
        foreach (var hw in _lhm.Instance.Hardware)
        {
            if (hw.HardwareType != HardwareType.Memory) continue;
            foreach (var s in hw.Sensors)
            {
                if (s.SensorType != SensorType.Load) continue;
                if (s.Value is not float v) continue;
                var pct = System.Math.Clamp((double)v, 0, 100);
                if (hw.Identifier.ToString() == "/ram") return pct;
                fallback ??= pct;
            }
        }
        return fallback;
    }
}
