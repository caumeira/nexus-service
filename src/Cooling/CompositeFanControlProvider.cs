using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Qos.Service.Models.Cooling;

namespace Qos.Service.Cooling;

/// <summary>
/// Aggregates the motherboard fan provider (LibreHardwareMonitor on Windows,
/// stub elsewhere) with the NP50 hub provider. Acts as the single
/// <see cref="IFanControlProvider"/> + <see cref="ICoolingProvider"/> the
/// rest of the service consumes, so the curve engine, REST routes, and
/// WebSocket broadcasts stay unchanged.
///
/// Routing is by channel-id prefix: anything starting with
/// <c>np50:</c> goes to the NP50 provider; everything else goes to the
/// motherboard provider. Temperature reads probe both because the spec
/// puts hub-only sensors under a different id namespace too.
///
/// Calibration only runs against the motherboard side — NP50 fans report
/// stable RPM and don't benefit from the ramp/hold dance.
/// </summary>
public sealed class CompositeFanControlProvider : IFanControlProvider, ICoolingProvider
{
    private readonly IFanControlProvider _motherboard;
    private readonly ICoolingProvider? _motherboardCooling;
    private readonly Np50CoolingProvider _np50;

    public CompositeFanControlProvider(IFanControlProvider motherboard, Np50CoolingProvider np50)
    {
        _motherboard = motherboard;
        _motherboardCooling = motherboard as ICoolingProvider;
        _np50 = np50;
    }

    // ── IFanControlProvider ──

    public IReadOnlyList<FanChannel> GetFanChannels()
    {
        var mb = _motherboard.GetFanChannels();
        var hub = _np50.GetFanChannels();
        if (hub.Count == 0) return mb;
        var combined = new List<FanChannel>(mb.Count + hub.Count);
        combined.AddRange(mb);
        combined.AddRange(hub);
        return combined;
    }

    public IReadOnlyList<TemperatureSource> GetTemperatureSources()
    {
        var mb = _motherboard.GetTemperatureSources();
        var hub = _np50.GetTemperatureSources();
        if (hub.Count == 0) return mb;
        var combined = new List<TemperatureSource>(mb.Count + hub.Count);
        combined.AddRange(mb);
        combined.AddRange(hub);
        return combined;
    }

    public float? ReadTemperature(string sensorId)
    {
        if (IsNp50Id(sensorId)) return _np50.ReadTemperature(sensorId);
        return _motherboard.ReadTemperature(sensorId);
    }

    public int SetFanSpeed(string channelId, int dutyPercent)
    {
        return IsNp50Id(channelId)
            ? _np50.SetFanSpeed(channelId, dutyPercent)
            : _motherboard.SetFanSpeed(channelId, dutyPercent);
    }

    public void DriveFanSpeed(string channelId, int dutyPercent)
    {
        if (IsNp50Id(channelId)) _np50.DriveFanSpeed(channelId, dutyPercent);
        else _motherboard.DriveFanSpeed(channelId, dutyPercent);
    }

    public void ReleaseFan(string channelId)
    {
        if (IsNp50Id(channelId)) _np50.ReleaseFan(channelId);
        else _motherboard.ReleaseFan(channelId);
    }

    public void ReleaseAll()
    {
        // Order matters only for telemetry clarity; both are idempotent.
        _motherboard.ReleaseAll();
        _np50.ReleaseAll();
    }

    public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
        IReadOnlyList<string> fanIds,
        IProgress<FanCalibrationProgress> progress,
        CancellationToken ct)
    {
        // Defer to the motherboard provider only. NP50 calibration is a no-op
        // (see Np50CoolingProvider.CalibrateAsync). Filter out NP50 ids so the
        // motherboard side doesn't fail on unrecognized channels.
        if (fanIds.Count == 0)
            return _motherboard.CalibrateAsync(fanIds, progress, ct);
        var motherboardOnly = new List<string>(fanIds.Count);
        foreach (var id in fanIds)
            if (!IsNp50Id(id)) motherboardOnly.Add(id);
        return _motherboard.CalibrateAsync(motherboardOnly, progress, ct);
    }

    // ── ICoolingProvider ──

    public IReadOnlyList<CoolingComponent> GetAll()
    {
        var mb = _motherboardCooling?.GetAll() ?? Array.Empty<CoolingComponent>();
        var hub = _np50.GetAll();
        if (hub.Count == 0) return mb;
        var combined = new List<CoolingComponent>(mb.Count + hub.Count);
        combined.AddRange(mb);
        combined.AddRange(hub);
        return combined;
    }

    private static bool IsNp50Id(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith("np50:", StringComparison.Ordinal);
}
