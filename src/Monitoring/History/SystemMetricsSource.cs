using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;

namespace Nexus.Service.Monitoring.History;

/// <summary>
/// Production IMetricsSource: CPU/memory from IPerformanceProvider, network
/// byte rates from NetworkRateReader, CPU temperature via SummarySensors,
/// per-GPU load/temperature from ISensorProvider.GetGpus, and per-channel
/// fan RPM/duty from IFanControlProvider.GetFanChannels - the same
/// always-on channel state CurveEngine reads at its own 1Hz tick, so this
/// adds no new hardware polling.
///
/// Each source below is independently try/caught so one failing read (e.g.
/// a GPU driver hiccup) nulls only its own MetricSample fields rather than
/// dropping the whole tick.
/// </summary>
public sealed class SystemMetricsSource : IMetricsSource
{
    private readonly IPerformanceProvider _performance;
    private readonly ISensorProvider _sensors;
    private readonly IFanControlProvider _fans;
    private readonly NetworkRateReader _network;

    public SystemMetricsSource(
        IPerformanceProvider performance, ISensorProvider sensors, IFanControlProvider fans, NetworkRateReader network)
    {
        _performance = performance;
        _sensors = sensors;
        _fans = fans;
        _network = network;
    }

    public async Task<MetricSample> SampleAsync(long tsSec, CancellationToken ct)
    {
        double? cpu = null;
        double? memory = null;
        try
        {
            var perf = await _performance.SampleAsync(ct).ConfigureAwait(false);
            cpu = perf.Cpu;
            memory = perf.Memory;
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[metrics-source] performance read failed: {ex.Message}");
        }

        double? netIn = null;
        double? netOut = null;
        try
        {
            var rate = _network.Read();
            netIn = rate.InBytesPerSec;
            netOut = rate.OutBytesPerSec;
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[metrics-source] network read failed: {ex.Message}");
        }

        double? cpuTemp = null;
        try
        {
            cpuTemp = SummarySensors.Value(_sensors, SummarySensorKind.CpuTemp);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[metrics-source] cpu temp read failed: {ex.Message}");
        }

        IReadOnlyList<GpuReading> gpus = Array.Empty<GpuReading>();
        try
        {
            gpus = ReadGpus();
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[metrics-source] gpu read failed: {ex.Message}");
        }

        IReadOnlyList<FanReading> fans = Array.Empty<FanReading>();
        try
        {
            fans = ReadFans();
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[metrics-source] fan read failed: {ex.Message}");
        }

        return new MetricSample(tsSec, cpu, memory, netIn, netOut, cpuTemp, gpus, fans);
    }

    private IReadOnlyList<GpuReading> ReadGpus()
    {
        var readouts = _sensors.GetGpus();
        if (readouts.Count == 0)
        {
            return Array.Empty<GpuReading>();
        }

        var result = new List<GpuReading>(readouts.Count);
        foreach (var g in readouts)
        {
            var load = FindSensorValue(g.Sensors, "Load", "Core");
            var temp = FindSensorValue(g.Sensors, "Temperature", "Core");
            result.Add(new GpuReading(MetricsHistory.SanitizeId(g.Id), g.Name, g.AdapterLuid, load, temp));
        }
        return result;
    }

    private IReadOnlyList<FanReading> ReadFans()
    {
        var channels = _fans.GetFanChannels();
        if (channels.Count == 0)
        {
            return Array.Empty<FanReading>();
        }

        var result = new List<FanReading>(channels.Count);
        foreach (var ch in channels)
        {
            result.Add(new FanReading(
                MetricsHistory.SanitizeId(ch.Id), ch.Name, ch.RpmUnavailable ? null : ch.Rpm, ch.DutyPercent));
        }
        return result;
    }

    // Prefers a sensor whose name mentions nameContains (the GPU core reading,
    // not a memory/hotspot/junction reading of the same type); falls back to
    // the first sensor of the requested type if no such match exists.
    private static double? FindSensorValue(IReadOnlyList<HardwareSensor> sensors, string type, string nameContains)
    {
        HardwareSensor? fallback = null;
        foreach (var s in sensors)
        {
            if (!string.Equals(s.Type, type, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            fallback ??= s;
            if (s.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            {
                return s.Value;
            }
        }
        return fallback?.Value;
    }
}
