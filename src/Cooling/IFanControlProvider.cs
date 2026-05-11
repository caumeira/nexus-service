using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Qos.Service.Models.Cooling;

namespace Qos.Service.Cooling;

/// <summary>
/// Write-path interface for fan control. Discovers controllable fan channels,
/// reads temperatures, and sets fan speeds via the hardware abstraction layer.
///
/// On Windows this is backed by LibreHardwareMonitor (motherboard SuperIO +
/// GPU fan headers). On macOS/Linux this returns empty data (no kernel-level
/// fan control APIs available).
/// </summary>
public interface IFanControlProvider
{
    /// <summary>All controllable fan channels discovered from hardware.</summary>
    IReadOnlyList<FanChannel> GetFanChannels();

    /// <summary>All available temperature sensors that can serve as curve input.</summary>
    IReadOnlyList<TemperatureSource> GetTemperatureSources();

    /// <summary>Read current temperature by sensor ID. Returns null if sensor not found.</summary>
    float? ReadTemperature(string sensorId);

    /// <summary>Set duty cycle (0-100) on a fan channel. Returns actual value set.</summary>
    int SetFanSpeed(string channelId, int dutyPercent);

    /// <summary>Release fan channel back to BIOS/automatic control.</summary>
    void ReleaseFan(string channelId);

    /// <summary>Release all fans back to BIOS/automatic control.</summary>
    void ReleaseAll();

    /// <summary>
    /// Calibrate the given fans by ramping duty 100%→0% and measuring RPM at
    /// each step. Empty fanIds = calibrate all discovered fans. Results are
    /// persisted to IConfigStore. Runs all fans in parallel.
    /// </summary>
    Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
        IReadOnlyList<string> fanIds,
        IProgress<FanCalibrationProgress> progress,
        CancellationToken ct);
}
