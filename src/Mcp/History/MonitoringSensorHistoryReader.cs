using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Monitoring.History;

namespace Nexus.Service.Mcp.History;

/// <summary>Which resolution a query_sensor_history series was served from.
/// Raw below the monitoring store's minute-rollup eligibility threshold
/// (BinaryMetricsHistoryStore.IsRollupEligible); OneMinute/FiveMinute above
/// it, split at the point on MetricsHistory.StepLadderSeconds where a
/// five-minute-wide slot first appears. Cosmetic only - QueryScalarsDecimated
/// and its siblings already pick the efficient physical path internally for
/// whatever stepSeconds they are given.</summary>
public enum AiHistoryTier
{
    Raw,
    OneMinute,
    FiveMinute,
}

public readonly record struct AiHistoryPointRow(long TUtcMs, double Value);

public sealed record AiHistorySeriesResult(
    string SensorId, string Name, string Unit, AiHistoryTier Tier, IReadOnlyList<AiHistoryPointRow> Points);

public sealed record AiHistorySensorSummaryRow(
    string SensorId, string Name, string Unit, double Min, double Max, double Avg, double Latest, long LatestAtUtcMs, int Samples);

/// <summary>
/// Serves query_sensor_history/get_history_summary from the always-on
/// monitoring store (IMetricsHistoryStore) instead of a dedicated AI sample
/// recorder - the store already samples every one of these fields at 1Hz for
/// the dashboard, so no separate collection exists for the assistant's
/// read path. Ids follow a fixed vocabulary decoupled from get_sensors' raw
/// hardware ids:
///
/// Fixed scalars: cpu.temp, cpu.load, mem.load, net.in, net.out, disk.read,
/// disk.write.
/// Per GPU: gpu.&lt;gpuId&gt;.load, gpu.&lt;gpuId&gt;.temp.
/// Per fan: fan.&lt;fanId&gt;.rpm, fan.&lt;fanId&gt;.duty.
/// Per storage/ram component: temp.&lt;componentId&gt;.
///
/// gpuId/fanId/componentId match IMetricsHistoryStore's own GpuId/FanId/
/// ComponentId (already MetricsHistory.SanitizeId-ed). KnownSensorIds and
/// Summarize only ever surface an id that has at least one non-null reading
/// in the queried/discovery window, so the assistant never sees an id it
/// cannot also query.
/// </summary>
public sealed class MonitoringSensorHistoryReader
{
    // Below this width a decimated query resolves to a raw per-second scan
    // (BinaryMetricsHistoryStore.IsRollupEligible); at or above it, to the
    // minute rollup. Every step MetricsDecimation.StepSecondsFor can return
    // is either below this or an exact multiple of it, so no separate
    // "round to a rollup-eligible step" pass is needed here.
    private const int RollupEligibleStepSeconds = 60;
    private const int FiveMinuteStepSeconds = 300;

    // How far back KnownSensorIds/the per-entity existence fallback look to
    // decide whether a dynamic (gpu/fan/temp) id has ever been recorded
    // recently enough to call "known" - there is no persistent per-entity
    // registry to check against the way the deleted sample store had.
    private static readonly TimeSpan DiscoveryWindow = TimeSpan.FromHours(24);
    private const int DiscoveryStepSeconds = 3600;

    // Resolution used internally for Summarize's own decimated queries;
    // unrelated to any caller-facing maxPoints.
    private const int SummaryResolutionPoints = 500;

    private readonly IMetricsHistoryStore _store;

    public MonitoringSensorHistoryReader(IMetricsHistoryStore store) => _store = store;

    /// <summary>Null when sensorId does not match the id vocabulary at all, or
    /// (for a gpu/fan/temp id) has no reading anywhere in the discovery
    /// window either. An empty Points list is a known sensor with no data in
    /// this particular window, not an error.</summary>
    public AiHistorySeriesResult? QuerySensorHistory(string sensorId, long fromUtcMs, long toUtcMs, int maxPoints)
    {
        var fromSec = fromUtcMs / 1000;
        var toSec = toUtcMs / 1000;
        var stepSeconds = ResolveStep(fromSec, toSec, maxPoints);

        var resolved = ResolveSeries(sensorId, fromSec, toSec, stepSeconds);
        if (resolved is null)
        {
            return null;
        }

        return new AiHistorySeriesResult(sensorId, resolved.Value.Name, resolved.Value.Unit, TierFor(stepSeconds), resolved.Value.Points);
    }

    /// <summary>One summary row per id that has at least one non-null reading
    /// in [fromUtcMs, toUtcMs].</summary>
    public IReadOnlyList<AiHistorySensorSummaryRow> Summarize(long fromUtcMs, long toUtcMs)
    {
        var fromSec = fromUtcMs / 1000;
        var toSec = toUtcMs / 1000;
        var stepSeconds = ResolveStep(fromSec, toSec, SummaryResolutionPoints);

        var result = new List<AiHistorySensorSummaryRow>();
        var scalars = _store.QueryScalarsDecimated(fromSec, toSec, stepSeconds);
        AddScalarSummary(result, scalars, "cpu.temp", "CPU Temperature", "C", s => s.CpuTempAvg, s => s.CpuTempMax);
        AddScalarSummary(result, scalars, "cpu.load", "CPU Load", "%", s => s.CpuAvg, s => s.CpuMax);
        AddScalarSummary(result, scalars, "mem.load", "Memory Load", "%", s => s.MemAvg, s => s.MemMax);
        AddScalarSummary(result, scalars, "net.in", "Network In", "B/s", s => s.NetInAvg, s => s.NetInMax);
        AddScalarSummary(result, scalars, "net.out", "Network Out", "B/s", s => s.NetOutAvg, s => s.NetOutMax);
        AddScalarSummary(result, scalars, "disk.read", "Disk Read", "B/s", s => s.DiskReadAvg, s => s.DiskReadMax);
        AddScalarSummary(result, scalars, "disk.write", "Disk Write", "B/s", s => s.DiskWriteAvg, s => s.DiskWriteMax);

        foreach (var group in _store.QueryGpuDecimated(fromSec, toSec, stepSeconds)
            .GroupBy(s => s.GpuId, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var name = group.First().Name;
            AddEntitySummary(result, group, $"gpu.{group.Key}.load", $"{name} Load", "%", s => s.LoadAvg, s => s.LoadMax, s => s.Slot);
            AddEntitySummary(result, group, $"gpu.{group.Key}.temp", $"{name} Temperature", "C", s => s.TempAvg, s => s.TempMax, s => s.Slot);
        }
        foreach (var group in _store.QueryFanDecimated(fromSec, toSec, stepSeconds)
            .GroupBy(s => s.FanId, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var name = group.First().Name;
            AddEntitySummary(result, group, $"fan.{group.Key}.rpm", $"{name} RPM", "RPM", s => s.RpmAvg, s => s.RpmMax, s => s.Slot);
            AddEntitySummary(result, group, $"fan.{group.Key}.duty", $"{name} Duty", "%", s => s.DutyAvg, s => s.DutyMax, s => s.Slot);
        }
        foreach (var group in _store.QueryComponentTempDecimated(fromSec, toSec, stepSeconds)
            .GroupBy(s => s.ComponentId, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var name = group.First().Name;
            AddEntitySummary(result, group, $"temp.{group.Key}", name, "C", s => s.Avg, s => s.Max, s => s.Slot);
        }
        return result;
    }

    /// <summary>Every id with at least one non-null reading within
    /// DiscoveryWindow of now.</summary>
    public IReadOnlyList<string> KnownSensorIds()
    {
        var (fromSec, toSec) = DiscoveryRange();
        var ids = new List<string>();

        var scalars = _store.QueryScalarsDecimated(fromSec, toSec, DiscoveryStepSeconds);
        if (scalars.Any(s => s.CpuTempAvg is not null)) { ids.Add("cpu.temp"); }
        if (scalars.Any(s => s.CpuAvg is not null)) { ids.Add("cpu.load"); }
        if (scalars.Any(s => s.MemAvg is not null)) { ids.Add("mem.load"); }
        if (scalars.Any(s => s.NetInAvg is not null)) { ids.Add("net.in"); }
        if (scalars.Any(s => s.NetOutAvg is not null)) { ids.Add("net.out"); }
        if (scalars.Any(s => s.DiskReadAvg is not null)) { ids.Add("disk.read"); }
        if (scalars.Any(s => s.DiskWriteAvg is not null)) { ids.Add("disk.write"); }

        foreach (var group in _store.QueryGpuDecimated(fromSec, toSec, DiscoveryStepSeconds)
            .GroupBy(s => s.GpuId, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (group.Any(s => s.LoadAvg is not null)) { ids.Add($"gpu.{group.Key}.load"); }
            if (group.Any(s => s.TempAvg is not null)) { ids.Add($"gpu.{group.Key}.temp"); }
        }
        foreach (var group in _store.QueryFanDecimated(fromSec, toSec, DiscoveryStepSeconds)
            .GroupBy(s => s.FanId, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (group.Any(s => s.RpmAvg is not null)) { ids.Add($"fan.{group.Key}.rpm"); }
            if (group.Any(s => s.DutyAvg is not null)) { ids.Add($"fan.{group.Key}.duty"); }
        }
        foreach (var group in _store.QueryComponentTempDecimated(fromSec, toSec, DiscoveryStepSeconds)
            .GroupBy(s => s.ComponentId, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (group.Any(s => s.Avg is not null)) { ids.Add($"temp.{group.Key}"); }
        }
        return ids;
    }

    private static int ResolveStep(long fromSec, long toSec, int maxPoints) =>
        MetricsDecimation.StepSecondsFor(Math.Max(0, toSec - fromSec), Math.Max(1, maxPoints));

    private static AiHistoryTier TierFor(int stepSeconds) =>
        stepSeconds < RollupEligibleStepSeconds ? AiHistoryTier.Raw
        : stepSeconds < FiveMinuteStepSeconds ? AiHistoryTier.OneMinute
        : AiHistoryTier.FiveMinute;

    private (long FromSec, long ToSec) DiscoveryRange()
    {
        var toMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var fromMs = toMs - (long)DiscoveryWindow.TotalMilliseconds;
        return (fromMs / 1000, toMs / 1000);
    }

    private readonly record struct SeriesData(string Name, string Unit, List<AiHistoryPointRow> Points);

    private SeriesData? ResolveSeries(string sensorId, long fromSec, long toSec, int stepSeconds)
    {
        switch (sensorId)
        {
            case "cpu.temp": return ScalarSeries(fromSec, toSec, stepSeconds, "CPU Temperature", "C", s => s.CpuTempAvg);
            case "cpu.load": return ScalarSeries(fromSec, toSec, stepSeconds, "CPU Load", "%", s => s.CpuAvg);
            case "mem.load": return ScalarSeries(fromSec, toSec, stepSeconds, "Memory Load", "%", s => s.MemAvg);
            case "net.in": return ScalarSeries(fromSec, toSec, stepSeconds, "Network In", "B/s", s => s.NetInAvg);
            case "net.out": return ScalarSeries(fromSec, toSec, stepSeconds, "Network Out", "B/s", s => s.NetOutAvg);
            case "disk.read": return ScalarSeries(fromSec, toSec, stepSeconds, "Disk Read", "B/s", s => s.DiskReadAvg);
            case "disk.write": return ScalarSeries(fromSec, toSec, stepSeconds, "Disk Write", "B/s", s => s.DiskWriteAvg);
        }

        if (sensorId.StartsWith("gpu.", StringComparison.Ordinal))
        {
            return ResolveGpuSeries(sensorId, fromSec, toSec, stepSeconds);
        }
        if (sensorId.StartsWith("fan.", StringComparison.Ordinal))
        {
            return ResolveFanSeries(sensorId, fromSec, toSec, stepSeconds);
        }
        if (sensorId.StartsWith("temp.", StringComparison.Ordinal))
        {
            return ResolveComponentSeries(sensorId, fromSec, toSec, stepSeconds);
        }
        return null;
    }

    private SeriesData ScalarSeries(
        long fromSec, long toSec, int stepSeconds, string name, string unit, Func<ScalarDecimatedSlot, double?> selector)
    {
        var points = _store.QueryScalarsDecimated(fromSec, toSec, stepSeconds)
            .Where(s => selector(s) is not null)
            .OrderBy(s => s.Slot)
            .Select(s => new AiHistoryPointRow(s.Slot * 1000, selector(s)!.Value))
            .ToList();
        return new SeriesData(name, unit, points);
    }

    private SeriesData? ResolveGpuSeries(string sensorId, long fromSec, long toSec, int stepSeconds)
    {
        var rest = sensorId[4..];
        string gpuId;
        string field;
        string unit;
        Func<GpuDecimatedSlot, double?> selector;
        if (rest.EndsWith(".load", StringComparison.Ordinal))
        {
            gpuId = rest[..^5];
            field = "Load";
            unit = "%";
            selector = s => s.LoadAvg;
        }
        else if (rest.EndsWith(".temp", StringComparison.Ordinal))
        {
            gpuId = rest[..^5];
            field = "Temperature";
            unit = "C";
            selector = s => s.TempAvg;
        }
        else
        {
            return null;
        }
        if (gpuId.Length == 0)
        {
            return null;
        }

        var slots = _store.QueryGpuDecimated(fromSec, toSec, stepSeconds)
            .Where(s => s.GpuId == gpuId)
            .OrderBy(s => s.Slot)
            .ToList();
        var name = slots.Count > 0 ? slots[0].Name : DiscoverGpuName(gpuId);
        if (name is null)
        {
            return null;
        }

        var points = slots
            .Where(s => selector(s) is not null)
            .Select(s => new AiHistoryPointRow(s.Slot * 1000, selector(s)!.Value))
            .ToList();
        return new SeriesData($"{name} {field}", unit, points);
    }

    private SeriesData? ResolveFanSeries(string sensorId, long fromSec, long toSec, int stepSeconds)
    {
        var rest = sensorId[4..];
        string fanId;
        string field;
        string unit;
        Func<FanDecimatedSlot, double?> selector;
        if (rest.EndsWith(".rpm", StringComparison.Ordinal))
        {
            fanId = rest[..^4];
            field = "RPM";
            unit = "RPM";
            selector = s => s.RpmAvg;
        }
        else if (rest.EndsWith(".duty", StringComparison.Ordinal))
        {
            fanId = rest[..^5];
            field = "Duty";
            unit = "%";
            selector = s => s.DutyAvg;
        }
        else
        {
            return null;
        }
        if (fanId.Length == 0)
        {
            return null;
        }

        var slots = _store.QueryFanDecimated(fromSec, toSec, stepSeconds)
            .Where(s => s.FanId == fanId)
            .OrderBy(s => s.Slot)
            .ToList();
        var name = slots.Count > 0 ? slots[0].Name : DiscoverFanName(fanId);
        if (name is null)
        {
            return null;
        }

        var points = slots
            .Where(s => selector(s) is not null)
            .Select(s => new AiHistoryPointRow(s.Slot * 1000, selector(s)!.Value))
            .ToList();
        return new SeriesData($"{name} {field}", unit, points);
    }

    private SeriesData? ResolveComponentSeries(string sensorId, long fromSec, long toSec, int stepSeconds)
    {
        var componentId = sensorId[5..];
        if (componentId.Length == 0)
        {
            return null;
        }

        var slots = _store.QueryComponentTempDecimated(fromSec, toSec, stepSeconds)
            .Where(s => s.ComponentId == componentId)
            .OrderBy(s => s.Slot)
            .ToList();
        var name = slots.Count > 0 ? slots[0].Name : DiscoverComponentName(componentId);
        if (name is null)
        {
            return null;
        }

        var points = slots
            .Where(s => s.Avg is not null)
            .Select(s => new AiHistoryPointRow(s.Slot * 1000, s.Avg!.Value))
            .ToList();
        return new SeriesData(name, "C", points);
    }

    private string? DiscoverGpuName(string gpuId)
    {
        var (fromSec, toSec) = DiscoveryRange();
        return _store.QueryGpuDecimated(fromSec, toSec, DiscoveryStepSeconds)
            .Where(s => s.GpuId == gpuId)
            .Select(s => s.Name)
            .FirstOrDefault();
    }

    private string? DiscoverFanName(string fanId)
    {
        var (fromSec, toSec) = DiscoveryRange();
        return _store.QueryFanDecimated(fromSec, toSec, DiscoveryStepSeconds)
            .Where(s => s.FanId == fanId)
            .Select(s => s.Name)
            .FirstOrDefault();
    }

    private string? DiscoverComponentName(string componentId)
    {
        var (fromSec, toSec) = DiscoveryRange();
        return _store.QueryComponentTempDecimated(fromSec, toSec, DiscoveryStepSeconds)
            .Where(s => s.ComponentId == componentId)
            .Select(s => s.Name)
            .FirstOrDefault();
    }

    // Min approximates the smallest per-slot average; the monitoring store's
    // decimated slots persist a per-slot avg and max only, no per-slot min.
    private static void AddEntitySummary<T>(
        List<AiHistorySensorSummaryRow> output, IEnumerable<T> slots, string id, string name, string unit,
        Func<T, double?> avgSelector, Func<T, double?> maxSelector, Func<T, long> slotSelector)
    {
        var withData = slots.Where(s => avgSelector(s) is not null).OrderBy(slotSelector).ToList();
        if (withData.Count == 0)
        {
            return;
        }
        var avgs = withData.Select(s => avgSelector(s)!.Value).ToList();
        var maxes = withData.Select(s => maxSelector(s) ?? avgSelector(s)!.Value).ToList();
        var last = withData[^1];
        output.Add(new AiHistorySensorSummaryRow(
            id, name, unit, avgs.Min(), maxes.Max(), avgs.Average(), avgSelector(last)!.Value, slotSelector(last) * 1000, withData.Count));
    }

    private static void AddScalarSummary(
        List<AiHistorySensorSummaryRow> output, IReadOnlyList<ScalarDecimatedSlot> slots, string id, string name, string unit,
        Func<ScalarDecimatedSlot, double?> avgSelector, Func<ScalarDecimatedSlot, double?> maxSelector) =>
        AddEntitySummary(output, slots, id, name, unit, avgSelector, maxSelector, s => s.Slot);
}
