using System;
using LibreHardwareMonitor.Hardware;

namespace Nexus.Service.Sensors;

/// <summary>
/// Shared singleton wrapping the LibreHardwareMonitor Computer instance.
/// Both the sensor provider and the fan control provider share this to avoid
/// resource duplication and driver conflicts.
///
/// Thread safety: the lock in Update() ensures only one caller updates at a
/// time. Reads of sensor.Value after an Update() are safe without locking
/// (they return the last-cached value).
/// </summary>
public sealed class LhmComputer : IDisposable
{
    private readonly Computer _computer;
    private readonly object _updateLock = new();
    private long _lastUpdateTicks;

    public LhmComputer()
    {
        // Open eagerly. LHM's Computer.Open() loads its kernel driver, walks
        // the ACPI / SMBIOS / PCI trees, and costs ~50 MB of working set plus
        // ~1 s of hardware enumeration. We previously deferred this to the
        // first sensor read, but for a hardware-monitoring service the cost
        // is best paid at boot: it's invisible (the service auto-starts at
        // logon long before anyone opens the dashboard), and deferring just
        // pushed the latency onto the first user interaction (Cooling tab
        // would show "no fans" for ~1 s while LHM enumerated). The deferred
        // open also raced PawnIO/SMU startup, leading to intermittent zero
        // readings on Zen 5 CPU temperature.
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsStorageEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true,
            IsNetworkEnabled = false,
            IsBatteryEnabled = false,
            IsPsuEnabled = false,
        };
        _computer.Open();
    }

    public Computer Instance => _computer;

    /// <summary>
    /// Update all hardware sensors. Thread-safe - concurrent callers are serialized.
    /// When <paramref name="minInterval"/> is provided, skips the update if the last
    /// refresh was more recent than the interval. This collapses redundant updates
    /// from multiple callers (MonitoringBroadcaster, CurveEngine, HTTP handlers)
    /// into at most one hardware iteration per interval.
    /// </summary>
    public void Update(TimeSpan? minInterval = null)
    {
        lock (_updateLock)
        {
            if (minInterval.HasValue)
            {
                var now = Environment.TickCount64;
                if (now - _lastUpdateTicks < (long)minInterval.Value.TotalMilliseconds)
                    return;
            }

            foreach (var hw in _computer.Hardware)
            {
                hw.Update();
                foreach (var sub in hw.SubHardware)
                    sub.Update();
            }
            _lastUpdateTicks = Environment.TickCount64;
        }
    }

    public void Dispose() => _computer.Close();
}
