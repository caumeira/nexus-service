using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Auth;
using Nexus.Service.Models;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;

namespace Nexus.Service.Routes;

/// <summary>
/// GET /monitoring/history: the metrics history read path. Merges
/// IMetricsHistoryStore's persisted rows with MetricsSampleBuffer's
/// unflushed tail, decimates every requested series onto one shared time
/// grid, and converts internal epoch-seconds timestamps to the wire's UTC
/// milliseconds. Pinned cross-repo contract with nexus-web's
/// useMetricHistory/monitoringHistory.ts - series ids, kinds, and the
/// {t, avg, max} point shape must not change on one side alone.
/// </summary>
public static class MonitoringHistoryRoutes
{
    private const int MinMaxPoints = 1;
    private const int MaxMaxPoints = 2000;
    private const int DefaultMaxPoints = 600;

    public static void MapMonitoringHistoryEndpoints(this WebApplication app)
    {
        app.MapGet("/monitoring/history", (
            long? from, long? to, int? maxPoints, string? series,
            IMetricsHistoryStore store, MetricsSampleBuffer buffer, ISensorProvider sensors) =>
        {
            if (from is null || to is null || to < from)
            {
                return Results.BadRequest(ApiResponse.Fail("from and to are required and to must be >= from"));
            }

            var fromSec = from.Value / 1000;
            var toSec = to.Value / 1000;
            var clampedMaxPoints = Math.Clamp(maxPoints ?? DefaultMaxPoints, MinMaxPoints, MaxMaxPoints);
            var seriesFilter = ParseSeriesFilter(series);

            try
            {
                // Buffer read first: a flush landing between the two calls
                // commits its samples to the store and then RemoveThroughs
                // them out of the buffer, so querying the store first could
                // miss those seconds in both reads. Reading the tail before
                // the store guarantees any sample dropped from the tail by
                // an intervening flush is already visible in the store read
                // that follows; MergeSamples's tail-wins-by-ts dedup handles
                // the overlap either way.
                var tailSamples = buffer.SnapshotRange(fromSec, toSec);
                var dbSamples = store.Query(fromSec, toSec);
                var adapterLuids = ResolveGpuAdapterLuids(sensors);
                var response = BuildHistoryResponse(
                    dbSamples, tailSamples, fromSec, toSec, clampedMaxPoints, seriesFilter, adapterLuids);
                return Results.Ok(response);
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[monitoring-history] query failed: {ex.Message}");
                return Results.Ok(new MetricsHistoryResponse { Supported = false });
            }
        }).AllowPanel();
    }

    private static HashSet<string>? ParseSeriesFilter(string? series)
    {
        if (string.IsNullOrWhiteSpace(series))
        {
            return null;
        }
        return series
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static Dictionary<string, string> ResolveGpuAdapterLuids(ISensorProvider sensors)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var g in sensors.GetGpus())
        {
            if (!string.IsNullOrEmpty(g.AdapterLuid))
            {
                result[MetricsHistory.SanitizeId(g.Id)] = g.AdapterLuid;
            }
        }
        return result;
    }

    internal static MetricsHistoryResponse BuildHistoryResponse(
        IReadOnlyList<MetricSample> dbSamples,
        IReadOnlyList<MetricSample> tailSamples,
        long fromSec, long toSec, int maxPoints,
        IReadOnlySet<string>? seriesFilter,
        IReadOnlyDictionary<string, string> gpuAdapterLuids)
    {
        var merged = MergeSamples(dbSamples, tailSamples);
        var stepSeconds = MetricsDecimation.StepSecondsFor(Math.Max(0, toSec - fromSec), maxPoints);

        var series = new List<MetricSeriesWire>();
        AddScalarSeries(series, merged, "cpu", "cpu", "CPU", s => s.CpuPercent, fromSec, toSec, stepSeconds, seriesFilter, wholeNumbers: false);
        AddScalarSeries(series, merged, "memory", "memory", "Memory", s => s.MemoryPercent, fromSec, toSec, stepSeconds, seriesFilter, wholeNumbers: false);
        AddScalarSeries(series, merged, "net-in", "net", "Network In", s => s.NetInBytesPerSec, fromSec, toSec, stepSeconds, seriesFilter, wholeNumbers: true);
        AddScalarSeries(series, merged, "net-out", "net", "Network Out", s => s.NetOutBytesPerSec, fromSec, toSec, stepSeconds, seriesFilter, wholeNumbers: true);
        AddScalarSeries(series, merged, "cpu-temp", "cpu-temp", "CPU Temperature", s => s.CpuTempC, fromSec, toSec, stepSeconds, seriesFilter, wholeNumbers: false);

        AddGpuSeries(series, merged, fromSec, toSec, stepSeconds, seriesFilter, gpuAdapterLuids);
        AddFanSeries(series, merged, fromSec, toSec, stepSeconds, seriesFilter);

        return new MetricsHistoryResponse
        {
            Supported = true,
            RetentionDays = MetricsHistory.RetentionDays,
            StepSeconds = stepSeconds,
            Series = series,
        };
    }

    // DB rows and the buffer's tail can overlap at the flush boundary (a
    // sample already committed to disk but not yet trimmed from the tail);
    // the tail wins since it is guaranteed to hold whatever SampleAsync last
    // produced for that second.
    private static List<MetricSample> MergeSamples(IReadOnlyList<MetricSample> db, IReadOnlyList<MetricSample> tail)
    {
        var map = new SortedDictionary<long, MetricSample>();
        foreach (var s in db)
        {
            map[s.TsSec] = s;
        }
        foreach (var s in tail)
        {
            map[s.TsSec] = s;
        }
        return map.Values.ToList();
    }

    private static bool MatchesFilter(string id, string kind, IReadOnlySet<string>? filter) =>
        filter is null || filter.Contains(id) || filter.Contains(kind);

    private static void AddScalarSeries(
        List<MetricSeriesWire> output, IReadOnlyList<MetricSample> merged,
        string id, string kind, string name, Func<MetricSample, double?> selector,
        long fromSec, long toSec, int stepSeconds, IReadOnlySet<string>? filter, bool wholeNumbers)
    {
        if (!MatchesFilter(id, kind, filter))
        {
            return;
        }

        var points = MetricsDecimation.Decimate(
            merged.Select(s => new MetricSamplePoint(s.TsSec, selector(s))), fromSec, toSec, stepSeconds);

        output.Add(new MetricSeriesWire
        {
            Id = id,
            Kind = kind,
            Name = name,
            Points = ToWirePoints(points, wholeNumbers),
        });
    }

    private static void AddGpuSeries(
        List<MetricSeriesWire> output, IReadOnlyList<MetricSample> merged,
        long fromSec, long toSec, int stepSeconds, IReadOnlySet<string>? filter,
        IReadOnlyDictionary<string, string> adapterLuids)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in merged)
        {
            foreach (var g in s.Gpus)
            {
                names[g.GpuId] = g.Name;
            }
        }

        foreach (var (gpuId, name) in names.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var luid = adapterLuids.GetValueOrDefault(gpuId);

            var loadId = $"gpu:{gpuId}";
            if (MatchesFilter(loadId, "gpu", filter))
            {
                var points = MetricsDecimation.Decimate(
                    merged.Select(s => new MetricSamplePoint(s.TsSec, FindGpu(s, gpuId)?.LoadPercent)),
                    fromSec, toSec, stepSeconds);
                output.Add(new MetricSeriesWire
                {
                    Id = loadId,
                    Kind = "gpu",
                    Name = name,
                    AdapterLuid = luid,
                    Points = ToWirePoints(points, wholeNumbers: false),
                });
            }

            var tempId = $"gpu-temp:{gpuId}";
            if (MatchesFilter(tempId, "gpu-temp", filter))
            {
                var points = MetricsDecimation.Decimate(
                    merged.Select(s => new MetricSamplePoint(s.TsSec, FindGpu(s, gpuId)?.TempC)),
                    fromSec, toSec, stepSeconds);
                output.Add(new MetricSeriesWire
                {
                    Id = tempId,
                    Kind = "gpu-temp",
                    Name = name,
                    AdapterLuid = luid,
                    Points = ToWirePoints(points, wholeNumbers: false),
                });
            }
        }
    }

    private static void AddFanSeries(
        List<MetricSeriesWire> output, IReadOnlyList<MetricSample> merged,
        long fromSec, long toSec, int stepSeconds, IReadOnlySet<string>? filter)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in merged)
        {
            foreach (var f in s.Fans)
            {
                names[f.FanId] = f.Name;
            }
        }

        foreach (var (fanId, name) in names.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var rpmId = $"fan:{fanId}";
            if (MatchesFilter(rpmId, "fan", filter))
            {
                var points = MetricsDecimation.Decimate(
                    merged.Select(s => new MetricSamplePoint(s.TsSec, FindFan(s, fanId)?.Rpm)),
                    fromSec, toSec, stepSeconds);
                output.Add(new MetricSeriesWire { Id = rpmId, Kind = "fan", Name = name, Points = ToWirePoints(points, wholeNumbers: true) });
            }

            var dutyId = $"fan-duty:{fanId}";
            if (MatchesFilter(dutyId, "fan-duty", filter))
            {
                var points = MetricsDecimation.Decimate(
                    merged.Select(s => new MetricSamplePoint(s.TsSec, FindFan(s, fanId)?.Duty)),
                    fromSec, toSec, stepSeconds);
                output.Add(new MetricSeriesWire { Id = dutyId, Kind = "fan-duty", Name = name, Points = ToWirePoints(points, wholeNumbers: false) });
            }
        }
    }

    private static GpuReading? FindGpu(MetricSample sample, string gpuId)
    {
        foreach (var g in sample.Gpus)
        {
            if (g.GpuId == gpuId)
            {
                return g;
            }
        }
        return null;
    }

    private static FanReading? FindFan(MetricSample sample, string fanId)
    {
        foreach (var f in sample.Fans)
        {
            if (f.FanId == fanId)
            {
                return f;
            }
        }
        return null;
    }

    private static List<MetricPoint> ToWirePoints(IReadOnlyList<MetricPoint> points, bool wholeNumbers) =>
        points.Select(p => p with
        {
            T = p.T * 1000,
            Avg = wholeNumbers ? Math.Round(p.Avg) : Math.Round(p.Avg, 1),
            Max = wholeNumbers ? Math.Round(p.Max) : Math.Round(p.Max, 1),
        }).ToList();
}

// ----- Wire response wrappers -----

public sealed record MetricSeriesWire
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Name { get; init; } = "";
    public string? AdapterLuid { get; init; }
    public IReadOnlyList<MetricPoint> Points { get; init; } = Array.Empty<MetricPoint>();
}

public sealed record MetricsHistoryResponse
{
    public bool Supported { get; init; } = true;
    public int RetentionDays { get; init; } = MetricsHistory.RetentionDays;
    public int StepSeconds { get; init; }
    public IReadOnlyList<MetricSeriesWire> Series { get; init; } = Array.Empty<MetricSeriesWire>();
}
