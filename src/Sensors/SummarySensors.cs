using System;
using System.Collections.Generic;
using Nexus.Service.Models.Sensors;

namespace Nexus.Service.Sensors;

/// <summary>Which "Quick" summary reading a caller wants derived from live sensors.</summary>
public enum SummarySensorKind
{
    CpuTemp,
    CpuUsage,
    GpuTemp,
    GpuUsage,
    MemoryUsage,
}

/// <summary>
/// Single derivation of the "Quick" summary sensor set (CPU temp/usage, GPU
/// temp/usage, memory usage), shared by the summary WebSocket topic and the
/// Lian Li wireless LCD reader so both pick the same underlying sensor.
/// </summary>
public static class SummarySensors
{
    private const string SummaryComponentId = "summary";
    private const string SummaryComponentName = "Quick";

    /// <summary>Builds the summary sensor list in fixed order (CpuTemp, CpuUsage,
    /// GpuTemp, GpuUsage, MemoryUsage), omitting any kind whose source sensor is absent.</summary>
    public static List<HardwareSensor> Build(ISensorProvider sensors)
        => BuildFrom(sensors.GetCpuSensors(), PrimaryGpuSensors(sensors), sensors.GetMemorySensors());

    /// <summary>Raw value of the picked source sensor for <paramref name="kind"/>, or null if absent.</summary>
    public static float? Value(ISensorProvider sensors, SummarySensorKind kind)
        => Pick(sensors.GetCpuSensors(), PrimaryGpuSensors(sensors), sensors.GetMemorySensors(), kind)?.Value;

    /// <summary>Same derivation as <see cref="Build(ISensorProvider)"/>, applied to sensor
    /// lists a caller already fetched, so a caller that has already read cpu/gpu/memory
    /// sensors this tick does not trigger a second underlying read for the summary set.</summary>
    internal static List<HardwareSensor> BuildFrom(
        IReadOnlyList<HardwareSensor> cpuSensors,
        IReadOnlyList<HardwareSensor> gpuSensors,
        IReadOnlyList<HardwareSensor> memorySensors)
    {
        var result = new List<HardwareSensor>(5);
        AddIfPresent(result, cpuSensors, gpuSensors, memorySensors, SummarySensorKind.CpuTemp);
        AddIfPresent(result, cpuSensors, gpuSensors, memorySensors, SummarySensorKind.CpuUsage);
        AddIfPresent(result, cpuSensors, gpuSensors, memorySensors, SummarySensorKind.GpuTemp);
        AddIfPresent(result, cpuSensors, gpuSensors, memorySensors, SummarySensorKind.GpuUsage);
        AddIfPresent(result, cpuSensors, gpuSensors, memorySensors, SummarySensorKind.MemoryUsage);
        return result;
    }

    private static void AddIfPresent(
        List<HardwareSensor> result,
        IReadOnlyList<HardwareSensor> cpuSensors,
        IReadOnlyList<HardwareSensor> gpuSensors,
        IReadOnlyList<HardwareSensor> memorySensors,
        SummarySensorKind kind)
    {
        var source = Pick(cpuSensors, gpuSensors, memorySensors, kind);
        if (source is null)
        {
            return;
        }
        result.Add(Clone(source, kind));
    }

    private static HardwareSensor? Pick(
        IReadOnlyList<HardwareSensor> cpuSensors,
        IReadOnlyList<HardwareSensor> gpuSensors,
        IReadOnlyList<HardwareSensor> memorySensors,
        SummarySensorKind kind) => kind switch
    {
        SummarySensorKind.CpuTemp => PreferElseFirst(cpuSensors, "Temperature", "Package"),
        SummarySensorKind.CpuUsage => PreferElseFirst(cpuSensors, "Load", "CPU Total"),
        SummarySensorKind.GpuTemp => PreferElseFirst(gpuSensors, "Temperature", "Core"),
        SummarySensorKind.GpuUsage => PreferElseFirst(gpuSensors, "Load", "Core"),
        SummarySensorKind.MemoryUsage => FindSensor(memorySensors, "Load", null),
        _ => null,
    };

    private static HardwareSensor Clone(HardwareSensor source, SummarySensorKind kind) => new()
    {
        Id = CanonicalId(kind),
        Name = CanonicalName(kind),
        Type = source.Type,
        Value = source.Value,
        Min = source.Min,
        Max = source.Max,
        Average = source.Average,
        Usage = source.Usage,
        TheoreticalMaximum = source.TheoreticalMaximum,
        Units = source.Units,
        Formatted = source.Formatted,
        FormattedMax = source.FormattedMax,
        FormattedMin = source.FormattedMin,
        FormattedAverage = source.FormattedAverage,
        FormattedUsage = source.FormattedUsage,
        Parent = new SensorParent { Id = SummaryComponentId, Name = SummaryComponentName },
    };

    private static string CanonicalId(SummarySensorKind kind) => kind switch
    {
        SummarySensorKind.CpuTemp => "summary/cpu-temp",
        SummarySensorKind.CpuUsage => "summary/cpu-usage",
        SummarySensorKind.GpuTemp => "summary/gpu-temp",
        SummarySensorKind.GpuUsage => "summary/gpu-usage",
        SummarySensorKind.MemoryUsage => "summary/memory-usage",
        _ => "",
    };

    private static string CanonicalName(SummarySensorKind kind) => kind switch
    {
        SummarySensorKind.CpuTemp => "CPU Temperature",
        SummarySensorKind.CpuUsage => "CPU Usage",
        SummarySensorKind.GpuTemp => "GPU Temperature",
        SummarySensorKind.GpuUsage => "GPU Usage",
        SummarySensorKind.MemoryUsage => "Memory Usage",
        _ => "",
    };

    private static IReadOnlyList<HardwareSensor> PrimaryGpuSensors(ISensorProvider sensors)
    {
        var gpus = sensors.GetGpus();
        for (var i = 0; i < gpus.Count; i++)
        {
            if (!gpus[i].Integrated)
            {
                return gpus[i].Sensors;
            }
        }
        return gpus.Count > 0 ? gpus[0].Sensors : Array.Empty<HardwareSensor>();
    }

    private static HardwareSensor? PreferElseFirst(IReadOnlyList<HardwareSensor> sensors, string type, string nameContains)
        => FindSensor(sensors, type, nameContains) ?? FindSensor(sensors, type, null);

    private static HardwareSensor? FindSensor(IReadOnlyList<HardwareSensor> sensors, string type, string? nameContains)
    {
        for (var i = 0; i < sensors.Count; i++)
        {
            var s = sensors[i];
            if (!string.Equals(s.Type, type, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (nameContains is null || s.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            {
                return s;
            }
        }
        return null;
    }
}
