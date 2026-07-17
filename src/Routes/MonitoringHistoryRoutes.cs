using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Nexus.Service.Activity;
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
///
/// GET /monitoring/privacy: which apps accessed microphone/webcam/location/
/// screen-capture, as sessions overlapping the requested window. Unsupported
/// off Windows (PrivacyAccessWatcher only runs there).
/// </summary>
public static class MonitoringHistoryRoutes
{
    private const int MinMaxPoints = 1;
    private const int MaxMaxPoints = 2000;
    private const int DefaultMaxPoints = 600;

    // Aligned with the metric_minutes/gpu_minutes/fan_minutes rollup's own
    // eligibility (SqliteMetricsHistoryStore.IsRollupEligible): below this,
    // QueryScalarsDecimated would fall back to a raw per-second GROUP BY,
    // which scans more rows than the raw-pull path at a narrow window, so
    // narrower requests stay on the untouched BuildHistoryResponse path;
    // at and above it every request is served from the rollup tier, which
    // scans a bounded number of pre-aggregated minute rows instead of
    // every raw second in the window.
    private const int DecimatedPathMinStepSeconds = 60;

    private const int MinMaxApps = 1;
    private const int MaxMaxApps = 100;
    private const int MinMaxAppPoints = 1;
    private const int MaxMaxAppPoints = 2000;

    // Query-timing log throttle: at most one line per route per this window,
    // regardless of request volume, so a live scrub session (many requests a
    // second while dragging) never floods the log.
    private static readonly TimeSpan TimingLogThrottle = TimeSpan.FromSeconds(5);
    private static long s_lastHistoryTimingLogTicks;
    private static long s_lastAppsTimingLogTicks;

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
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var adapterLuids = ResolveGpuAdapterLuids(sensors);
                var stepSeconds = MetricsDecimation.StepSecondsFor(Math.Max(0, toSec - fromSec), clampedMaxPoints);

                MetricsHistoryResponse response;
                if (stepSeconds >= DecimatedPathMinStepSeconds)
                {
                    // The GROUP BY's own overhead (temp b-tree sort, three
                    // separate round trips) measurably exceeds the raw path's
                    // cost at a moderate reduction ratio, and only wins once
                    // the window is wide enough - DecimatedPathMinStepSeconds
                    // is picked from that measured crossover, not from "any
                    // reduction is happening".
                    var tailSamples = buffer.SnapshotRange(fromSec, toSec);
                    var dbScalars = store.QueryScalarsDecimated(fromSec, toSec, stepSeconds);
                    var dbGpu = store.QueryGpuDecimated(fromSec, toSec, stepSeconds);
                    var dbFan = store.QueryFanDecimated(fromSec, toSec, stepSeconds);
                    response = BuildDecimatedHistoryResponse(
                        dbScalars, dbGpu, dbFan, tailSamples, fromSec, toSec, stepSeconds, seriesFilter, adapterLuids);
                }
                else
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
                    response = BuildHistoryResponse(
                        dbSamples, tailSamples, fromSec, toSec, clampedMaxPoints, seriesFilter, adapterLuids);
                }

                LogTimingThrottled(
                    ref s_lastHistoryTimingLogTicks, "monitoring-history",
                    stopwatch.ElapsedMilliseconds, toSec - fromSec, stepSeconds);
                return Results.Ok(response);
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[monitoring-history] query failed: {ex.Message}");
                return Results.Ok(new MetricsHistoryResponse { Supported = false });
            }
        }).AllowPanel();

        app.MapGet("/monitoring/privacy", (long? from, long? to, IPrivacySessionStore store) =>
        {
            if (from is null || to is null || to < from)
            {
                return Results.BadRequest(ApiResponse.Fail("from and to are required and to must be >= from"));
            }

            if (!OperatingSystem.IsWindows())
            {
                return Results.Ok(new PrivacyAccessResponse { Supported = false });
            }

            try
            {
                var fromSec = from.Value / 1000;
                var toSec = to.Value / 1000;
                var sessions = store.Query(fromSec, toSec);
                return Results.Ok(BuildPrivacyResponse(sessions, fromSec, toSec));
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[monitoring-privacy] query failed: {ex.Message}");
                return Results.Ok(new PrivacyAccessResponse { Supported = false });
            }
        }).AllowPanel();

        app.MapGet("/monitoring/history/apps", (
            long? from, long? to, string? series, string? process, int? maxApps, int? maxPoints,
            IAppUsageHistoryStore appStore, AppSampleBuffer appBuffer, ProcessMonitor processes) =>
        {
            if (from is null || to is null || to < from || string.IsNullOrWhiteSpace(series))
            {
                return Results.BadRequest(ApiResponse.Fail("from, to, and series are required and to must be >= from"));
            }

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var fromSec = from.Value / 1000;
                var toSec = to.Value / 1000;
                var clampedMaxApps = Math.Clamp(maxApps ?? MetricsHistory.DefaultMaxApps, MinMaxApps, MaxMaxApps);
                var clampedMaxPoints = Math.Clamp(maxPoints ?? MetricsHistory.DefaultMaxAppPoints, MinMaxAppPoints, MaxMaxAppPoints);

                // Buffer read first, matching the sibling /monitoring/history
                // route: a flush landing between the two reads commits its
                // samples to the store and then RemoveThroughs them out of
                // the buffer, so reading the store first can miss those
                // ticks in both reads. Reading the tail first guarantees
                // anything a subsequent flush drops from the buffer is
                // already visible in every store read below; MergeAppTail's
                // tail-wins-by-ts dedup handles the overlap either way.
                // Apps.Count > 0 matches what the store persists: a tick's
                // AppMetricSample only ever produces app_*_seconds rows when
                // it has apps, so a tail tick with none would stop counting
                // as sampled the moment it flushed - counting it here too
                // keeps expectedTicks stable across that boundary.
                var tailForMetric = appBuffer.SnapshotRange(fromSec, toSec)
                    .Select(t => (t.TsSec, Metric: ResolveTailMetric(t, series)))
                    .Where(t => t.Metric is not null && t.Metric.Apps.Count > 0)
                    .Select(t => (t.TsSec, Metric: t.Metric!))
                    .ToList();

                // Ticks the metric was sampled across db+tail, ts-deduped so
                // a tick straddling a flush boundary counts once - the same
                // window-average denominator QueryTopApps uses, extended to
                // cover ticks the tail has that the db doesn't have yet.
                var sampledTicks = new HashSet<long>(appStore.QuerySampledTicks(series, fromSec, toSec));
                foreach (var (ts, _) in tailForMetric)
                {
                    sampledTicks.Add(ts);
                }
                var expectedTicks = sampledTicks.Count;

                List<AppWindowStat> topApps;
                var seriesByApp = new Dictionary<string, IReadOnlyList<AppRawPoint>>(StringComparer.OrdinalIgnoreCase);

                if (!string.IsNullOrWhiteSpace(process))
                {
                    // The slideout asks for one specific process, which need
                    // not be in the metric's top-N - bypass ranking entirely
                    // and answer only for the requested name. QueryAppSeries
                    // matches case-insensitively but returns no name of its
                    // own, so the response echoes the requested casing
                    // rather than the store's canonical (first-seen) casing;
                    // resolving that would cost a dedicated lookup this path
                    // exists to avoid.
                    topApps = new List<AppWindowStat>();
                    if (expectedTicks > 0)
                    {
                        var merged = MergeAppTail(appStore.QueryAppSeries(series, process, fromSec, toSec), tailForMetric, process);
                        if (merged.Count > 0)
                        {
                            var sum = merged.Sum(p => p.Value ?? 0);
                            var max = merged.Max(p => p.Value ?? double.MinValue);
                            topApps.Add(new AppWindowStat(process, sum / expectedTicks, max));
                            seriesByApp[process] = merged;
                        }
                    }
                }
                else
                {
                    // Db-only ranking is a candidate pool (the tail is at most a
                    // few AppSampleIntervalSeconds ticks, negligible against any
                    // window wide enough for the db side to matter); every
                    // candidate's final avg/max/points is recomputed below from
                    // the db+tail merge, so a db-side ranking miss only costs a
                    // wasted QueryAppSeries call, never a wrong number. Each
                    // candidate is one sequential QueryAppSeries round trip, so
                    // this scales with maxApps (bounded by MaxMaxApps) plus a
                    // handful of tail-only names, not with window width.
                    var dbTopApps = appStore.QueryTopApps(series, fromSec, toSec, clampedMaxApps);

                    var candidateNames = new HashSet<string>(dbTopApps.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
                    foreach (var (_, metric) in tailForMetric)
                    {
                        foreach (var a in metric.Apps)
                        {
                            candidateNames.Add(a.Name);
                        }
                    }

                    var mergedApps = new List<AppWindowStat>();
                    if (expectedTicks > 0)
                    {
                        foreach (var name in candidateNames)
                        {
                            var merged = MergeAppTail(appStore.QueryAppSeries(series, name, fromSec, toSec), tailForMetric, name);
                            if (merged.Count == 0)
                            {
                                continue;
                            }
                            var sum = merged.Sum(p => p.Value ?? 0);
                            var max = merged.Max(p => p.Value ?? double.MinValue);
                            mergedApps.Add(new AppWindowStat(name, sum / expectedTicks, max));
                            seriesByApp[name] = merged;
                        }
                    }
                    topApps = mergedApps.OrderByDescending(a => a.Avg).Take(clampedMaxApps).ToList();
                }

                var liveStartedAt = ResolveLiveStartedAtByName(processes.GetProcesses());

                var response = BuildAppsHistoryResponse(
                    topApps, seriesByApp, liveStartedAt,
                    isGpuMetric: series == "gpu" || series.StartsWith("gpu:", StringComparison.Ordinal),
                    fromSec, toSec, clampedMaxPoints);

                LogTimingThrottled(
                    ref s_lastAppsTimingLogTicks, "monitoring-history-apps",
                    stopwatch.ElapsedMilliseconds, toSec - fromSec, topApps.Count);
                return Results.Ok(response);
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[monitoring-history-apps] query failed: {ex.Message}");
                return Results.Ok(new AppUsageHistoryResponse { Supported = false });
            }
        }).AllowPanel();

        app.MapGet("/monitoring/process-icon", (
            string? name, HttpContext ctx, ProcessMonitor processes,
            IProcessIconProvider iconProvider, ProcessIconCache cache) =>
        {
            if (string.IsNullOrEmpty(name))
            {
                return Results.BadRequest();
            }

            var exePath = processes.ResolveExecutablePath(name);
            if (exePath is null)
            {
                return Results.NotFound();
            }

            byte[] bytes;
            if (cache.TryGet(exePath, out var cached))
            {
                bytes = cached;
            }
            else
            {
                var extracted = iconProvider.GetIcon(exePath);
                if (extracted is null)
                {
                    // Extraction could not even be attempted (helper not
                    // connected yet, RPC timeout) - a transient state, not
                    // a verdict on this exe; do not cache it as empty.
                    return Results.NotFound();
                }
                bytes = extracted;
                cache.Set(exePath, bytes);
            }

            if (bytes.Length == 0)
            {
                return Results.NotFound();
            }

            // Content hash as the ETag, same pattern as GET /shortcuts/icon:
            // Results.File's entityTag drives the framework's conditional-GET
            // handling, so a matching If-None-Match short-circuits to a
            // bodyless 304. Icons are immutable per path for this service's
            // lifetime, so the cache is long-lived.
            var hash = Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
            var etag = new EntityTagHeaderValue($"\"{hash}\"");
            ctx.Response.Headers.CacheControl = "private, max-age=86400, immutable";
            return Results.File(bytes, "image/png", entityTag: etag);
        }).AllowPanel();
    }

    private static IReadOnlyDictionary<string, long> ResolveLiveStartedAtByName(IReadOnlyList<ProcessInfo> procs)
    {
        // OrdinalIgnoreCase: the app name stored in app_series (the key
        // BuildAppsHistoryResponse looks up against) keeps whatever casing
        // was first observed, which need not match the live snapshot's
        // current casing.
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, agg) in ProcessAggregation.GroupByName(procs))
        {
            if (agg.StartedAtMs is { } started)
            {
                result[name] = started;
            }
        }
        return result;
    }

    // "cpu"/"memory" (and a specific "gpu:<id>"/"vram:<id>") match the
    // tick's one sample with that exact metric id, same as before this
    // helper existed. Bare "gpu"/"vram" instead aggregate every matching
    // "gpu:<id>"/"vram:<id>" sample in the tick, mirroring
    // SqliteMetricsHistoryStore/InMemoryMetricsHistoryStore's unfiltered
    // app_gpu_seconds/app_vram_seconds query on the persisted side.
    internal static AppMetricSample? ResolveTailMetric(AppUsageTick tick, string series) =>
        series switch
        {
            "gpu" => AggregateGpuMetrics(tick),
            "vram" => AggregateVramMetrics(tick),
            _ => tick.Metrics.FirstOrDefault(m => m.Metric == series),
        };

    // Sums each app's value (and vram) across every per-adapter gpu:<id>
    // sample in one tick, so a process using two adapters reads as one
    // combined value for that tick - the tail-side mirror of QueryTopApps/
    // QueryAppSeries' SQL pre-aggregation. Null when the tick carries no gpu
    // sample at all, distinct from an empty Apps list (no gpu-active
    // process that tick), matching the "no data this tick" vs "sampled with
    // nothing to report" distinction the store queries already draw.
    internal static AppMetricSample? AggregateGpuMetrics(AppUsageTick tick)
    {
        var gpuSamples = tick.Metrics.Where(m => m.Metric.StartsWith("gpu:", StringComparison.Ordinal)).ToList();
        if (gpuSamples.Count == 0)
        {
            return null;
        }

        var order = new List<string>();
        var sums = new Dictionary<string, (double Value, double? Vram)>(StringComparer.OrdinalIgnoreCase);
        foreach (var sample in gpuSamples)
        {
            foreach (var a in sample.Apps)
            {
                if (sums.TryGetValue(a.Name, out var acc))
                {
                    sums[a.Name] = (acc.Value + a.Value, MetricsHistory.SumNullable(acc.Vram, a.VramMb));
                }
                else
                {
                    sums[a.Name] = (a.Value, a.VramMb);
                    order.Add(a.Name);
                }
            }
        }

        var points = order.Select(name => new AppUsagePoint(name, sums[name].Value, sums[name].Vram)).ToList();
        return new AppMetricSample("gpu", points);
    }

    // Sums each app's value across every per-adapter vram:<id> sample in one
    // tick, the vram counterpart of AggregateGpuMetrics. The reading itself
    // is the VRAM value (no side-channel VramMb to also sum), so each point
    // carries a null VramMb.
    internal static AppMetricSample? AggregateVramMetrics(AppUsageTick tick)
    {
        var vramSamples = tick.Metrics.Where(m => m.Metric.StartsWith("vram:", StringComparison.Ordinal)).ToList();
        if (vramSamples.Count == 0)
        {
            return null;
        }

        var order = new List<string>();
        var sums = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var sample in vramSamples)
        {
            foreach (var a in sample.Apps)
            {
                if (sums.TryGetValue(a.Name, out var acc))
                {
                    sums[a.Name] = acc + a.Value;
                }
                else
                {
                    sums[a.Name] = a.Value;
                    order.Add(a.Name);
                }
            }
        }

        var points = order.Select(name => new AppUsagePoint(name, sums[name], null)).ToList();
        return new AppMetricSample("vram", points);
    }

    // Db points and the buffered tail can overlap at the flush boundary;
    // the tail wins by ts (same precedence as MergeSamples), and a name
    // match is case-insensitive to match app_series' COLLATE NOCASE key.
    internal static List<AppRawPoint> MergeAppTail(
        IReadOnlyList<AppRawPoint> dbPoints,
        IReadOnlyList<(long TsSec, AppMetricSample Metric)> tailForMetric,
        string name)
    {
        var map = new SortedDictionary<long, AppRawPoint>();
        foreach (var p in dbPoints)
        {
            map[p.TsSec] = p;
        }
        foreach (var (ts, metric) in tailForMetric)
        {
            var point = metric.Apps.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
            if (point is not null)
            {
                map[ts] = new AppRawPoint(ts, point.Value, point.VramMb);
            }
        }
        return map.Values.ToList();
    }

    // Pure and directly unit-tested: every input is plain data the route
    // handler above already fetched (store queries + the live process
    // snapshot), so this has no I/O of its own.
    internal static AppUsageHistoryResponse BuildAppsHistoryResponse(
        IReadOnlyList<AppWindowStat> topApps,
        IReadOnlyDictionary<string, IReadOnlyList<AppRawPoint>> seriesByApp,
        IReadOnlyDictionary<string, long> liveStartedAtByName,
        bool isGpuMetric,
        long fromSec, long toSec, int maxPoints)
    {
        var stepSeconds = MetricsDecimation.StepSecondsFor(Math.Max(0, toSec - fromSec), maxPoints);

        var apps = new List<AppHistoryEntryWire>(topApps.Count);
        foreach (var stat in topApps)
        {
            var raw = seriesByApp.TryGetValue(stat.Name, out var s) ? s : Array.Empty<AppRawPoint>();
            var points = MetricsDecimation.Decimate(
                raw.Select(p => new MetricSamplePoint(p.TsSec, p.Value)), fromSec, toSec, stepSeconds);

            double? vramAvgMb = null;
            if (isGpuMetric)
            {
                var vramValues = raw.Where(p => p.VramMb is not null).Select(p => p.VramMb!.Value).ToList();
                if (vramValues.Count > 0)
                {
                    vramAvgMb = Math.Round(vramValues.Average(), 1);
                }
            }

            apps.Add(new AppHistoryEntryWire
            {
                Name = stat.Name,
                StartedAtMs = liveStartedAtByName.TryGetValue(stat.Name, out var started) ? started : null,
                Avg = Math.Round(stat.Avg, 1),
                Max = Math.Round(stat.Max, 1),
                VramAvgMb = vramAvgMb,
                Points = points.Select(p => new AppHistoryPointWire { T = p.T * 1000, Avg = Math.Round(p.Avg, 1) }).ToList(),
            });
        }

        return new AppUsageHistoryResponse { Supported = true, Apps = apps };
    }

    private static void LogTimingThrottled(ref long lastLogTicks, string route, long elapsedMs, long windowSeconds, int contextCount)
    {
        var nowTicks = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref lastLogTicks);
        if (nowTicks - last < TimingLogThrottle.Ticks)
        {
            return;
        }
        if (Interlocked.CompareExchange(ref lastLogTicks, nowTicks, last) != last)
        {
            return;
        }
        ServiceLog.Info($"[{route}] query took {elapsedMs}ms window={windowSeconds}s n={contextCount}");
    }

    // Wide-window fast path: db slots already carry avg/max per field (SQL
    // GROUP BY, see IMetricsHistoryStore.QueryScalarsDecimated/QueryGpuDecimated/
    // QueryFanDecimated), so only the small RAM tail needs C# decimation -
    // reusing MetricsDecimation.Decimate exactly as the narrow-window path
    // does. Same MetricsHistoryResponse shape as BuildHistoryResponse; kept
    // as a separate function rather than folded into it so the existing
    // raw-sample-based tests and call sites are untouched.
    internal static MetricsHistoryResponse BuildDecimatedHistoryResponse(
        IReadOnlyList<ScalarDecimatedSlot> dbScalars,
        IReadOnlyList<GpuDecimatedSlot> dbGpu,
        IReadOnlyList<FanDecimatedSlot> dbFan,
        IReadOnlyList<MetricSample> tailSamples,
        long fromSec, long toSec, int stepSeconds,
        IReadOnlySet<string>? seriesFilter,
        IReadOnlyDictionary<string, string> gpuAdapterLuids)
    {
        var series = new List<MetricSeriesWire>();

        AddDecimatedScalarSeries(series, "cpu", "cpu", "CPU",
            dbScalars.Select(s => (s.Slot, s.CpuAvg, s.CpuMax)), s => s.CpuPercent,
            tailSamples, fromSec, toSec, stepSeconds, seriesFilter, wholeNumbers: false);
        AddDecimatedScalarSeries(series, "memory", "memory", "Memory",
            dbScalars.Select(s => (s.Slot, s.MemAvg, s.MemMax)), s => s.MemoryPercent,
            tailSamples, fromSec, toSec, stepSeconds, seriesFilter, wholeNumbers: false);
        AddDecimatedScalarSeries(series, "net-in", "net", "Network In",
            dbScalars.Select(s => (s.Slot, s.NetInAvg, s.NetInMax)), s => s.NetInBytesPerSec,
            tailSamples, fromSec, toSec, stepSeconds, seriesFilter, wholeNumbers: true);
        AddDecimatedScalarSeries(series, "net-out", "net", "Network Out",
            dbScalars.Select(s => (s.Slot, s.NetOutAvg, s.NetOutMax)), s => s.NetOutBytesPerSec,
            tailSamples, fromSec, toSec, stepSeconds, seriesFilter, wholeNumbers: true);
        AddDecimatedScalarSeries(series, "cpu-temp", "cpu-temp", "CPU Temperature",
            dbScalars.Select(s => (s.Slot, s.CpuTempAvg, s.CpuTempMax)), s => s.CpuTempC,
            tailSamples, fromSec, toSec, stepSeconds, seriesFilter, wholeNumbers: false);

        AddDecimatedGpuSeries(series, dbGpu, tailSamples, fromSec, toSec, stepSeconds, seriesFilter, gpuAdapterLuids);
        AddDecimatedFanSeries(series, dbFan, tailSamples, fromSec, toSec, stepSeconds, seriesFilter);

        return new MetricsHistoryResponse
        {
            Supported = true,
            RetentionDays = MetricsHistory.RetentionDays,
            StepSeconds = stepSeconds,
            Series = series,
        };
    }

    private static void AddDecimatedScalarSeries(
        List<MetricSeriesWire> output, string id, string kind, string name,
        IEnumerable<(long Slot, double? Avg, double? Max)> dbSlots, Func<MetricSample, double?> tailSelector,
        IReadOnlyList<MetricSample> tailSamples, long fromSec, long toSec, int stepSeconds,
        IReadOnlySet<string>? filter, bool wholeNumbers)
    {
        if (!MatchesFilter(id, kind, filter))
        {
            return;
        }

        var tailPoints = MetricsDecimation.Decimate(
            tailSamples.Select(s => new MetricSamplePoint(s.TsSec, tailSelector(s))), fromSec, toSec, stepSeconds);
        var merged = MergeDecimatedSlots(dbSlots, tailPoints);

        output.Add(new MetricSeriesWire { Id = id, Kind = kind, Name = name, Points = ToWirePoints(merged, wholeNumbers) });
    }

    private static void AddDecimatedGpuSeries(
        List<MetricSeriesWire> output, IReadOnlyList<GpuDecimatedSlot> dbGpu, IReadOnlyList<MetricSample> tailSamples,
        long fromSec, long toSec, int stepSeconds, IReadOnlySet<string>? filter,
        IReadOnlyDictionary<string, string> adapterLuids)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in dbGpu)
        {
            names[s.GpuId] = s.Name;
        }
        foreach (var s in tailSamples)
        {
            foreach (var g in s.Gpus)
            {
                names[g.GpuId] = g.Name;
            }
        }

        foreach (var (gpuId, name) in names.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var luid = adapterLuids.GetValueOrDefault(gpuId);
            var dbForGpu = dbGpu.Where(s => s.GpuId == gpuId).ToList();

            var loadId = $"gpu:{gpuId}";
            if (MatchesFilter(loadId, "gpu", filter))
            {
                var tailPoints = MetricsDecimation.Decimate(
                    tailSamples.Select(s => new MetricSamplePoint(s.TsSec, FindGpu(s, gpuId)?.LoadPercent)),
                    fromSec, toSec, stepSeconds);
                var merged = MergeDecimatedSlots(dbForGpu.Select(s => (s.Slot, s.LoadAvg, s.LoadMax)), tailPoints);
                output.Add(new MetricSeriesWire
                {
                    Id = loadId,
                    Kind = "gpu",
                    Name = name,
                    AdapterLuid = luid,
                    Points = ToWirePoints(merged, wholeNumbers: false),
                });
            }

            var tempId = $"gpu-temp:{gpuId}";
            if (MatchesFilter(tempId, "gpu-temp", filter))
            {
                var tailPoints = MetricsDecimation.Decimate(
                    tailSamples.Select(s => new MetricSamplePoint(s.TsSec, FindGpu(s, gpuId)?.TempC)),
                    fromSec, toSec, stepSeconds);
                var merged = MergeDecimatedSlots(dbForGpu.Select(s => (s.Slot, s.TempAvg, s.TempMax)), tailPoints);
                output.Add(new MetricSeriesWire
                {
                    Id = tempId,
                    Kind = "gpu-temp",
                    Name = name,
                    AdapterLuid = luid,
                    Points = ToWirePoints(merged, wholeNumbers: false),
                });
            }
        }
    }

    private static void AddDecimatedFanSeries(
        List<MetricSeriesWire> output, IReadOnlyList<FanDecimatedSlot> dbFan, IReadOnlyList<MetricSample> tailSamples,
        long fromSec, long toSec, int stepSeconds, IReadOnlySet<string>? filter)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in dbFan)
        {
            names[s.FanId] = s.Name;
        }
        foreach (var s in tailSamples)
        {
            foreach (var f in s.Fans)
            {
                names[f.FanId] = f.Name;
            }
        }

        foreach (var (fanId, name) in names.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var dbForFan = dbFan.Where(s => s.FanId == fanId).ToList();

            var rpmId = $"fan:{fanId}";
            if (MatchesFilter(rpmId, "fan", filter))
            {
                var tailPoints = MetricsDecimation.Decimate(
                    tailSamples.Select(s => new MetricSamplePoint(s.TsSec, (double?)FindFan(s, fanId)?.Rpm)),
                    fromSec, toSec, stepSeconds);
                var merged = MergeDecimatedSlots(dbForFan.Select(s => (s.Slot, s.RpmAvg, s.RpmMax)), tailPoints);
                output.Add(new MetricSeriesWire { Id = rpmId, Kind = "fan", Name = name, Points = ToWirePoints(merged, wholeNumbers: true) });
            }

            var dutyId = $"fan-duty:{fanId}";
            if (MatchesFilter(dutyId, "fan-duty", filter))
            {
                var tailPoints = MetricsDecimation.Decimate(
                    tailSamples.Select(s => new MetricSamplePoint(s.TsSec, (double?)FindFan(s, fanId)?.Duty)),
                    fromSec, toSec, stepSeconds);
                var merged = MergeDecimatedSlots(dbForFan.Select(s => (s.Slot, s.DutyAvg, s.DutyMax)), tailPoints);
                output.Add(new MetricSeriesWire { Id = dutyId, Kind = "fan-duty", Name = name, Points = ToWirePoints(merged, wholeNumbers: false) });
            }
        }
    }

    // Db slots and the tail can overlap at the flush boundary; the tail wins
    // whole-slot (not per-second like MergeSamples) since it only ever spans
    // the buffered tail - at most the window's right edge or two, which is
    // inherently a live/partial reading either way rather than a fixed
    // historical average.
    private static List<MetricPoint> MergeDecimatedSlots(
        IEnumerable<(long Slot, double? Avg, double? Max)> dbSlots, IReadOnlyList<MetricPoint> tailPoints)
    {
        var map = new SortedDictionary<long, MetricPoint>();
        foreach (var (slot, avg, max) in dbSlots)
        {
            if (avg is { } a && max is { } m)
            {
                map[slot] = new MetricPoint(slot, a, m);
            }
        }
        foreach (var p in tailPoints)
        {
            map[p.T] = p;
        }
        return map.Values.ToList();
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

    // Sessions are filtered here (not left solely to the store's own WHERE
    // clause) so this stays a pure, directly testable overlap check: an open
    // session (EndUtcSec null) overlaps whenever its start is at or before
    // toSec, since it has no upper bound yet.
    internal static PrivacyAccessResponse BuildPrivacyResponse(
        IReadOnlyList<PrivacySession> sessions, long fromSec, long toSec) =>
        new()
        {
            Supported = true,
            RetentionDays = PrivacyAccess.RetentionDays,
            Sessions = sessions
                .Where(s => s.StartUtcSec <= toSec && (s.EndUtcSec is null || s.EndUtcSec >= fromSec))
                .OrderBy(s => s.StartUtcSec)
                .Select(s => new PrivacySessionWire
                {
                    App = s.AppId,
                    Capability = s.Capability,
                    Start = s.StartUtcSec * 1000,
                    End = s.EndUtcSec is { } end ? end * 1000 : null,
                })
                .ToList(),
        };
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

public sealed record PrivacySessionWire
{
    public string App { get; init; } = "";
    public string Capability { get; init; } = "";
    public long Start { get; init; }
    public long? End { get; init; }
}

public sealed record PrivacyAccessResponse
{
    public bool Supported { get; init; } = true;
    public int RetentionDays { get; init; } = PrivacyAccess.RetentionDays;
    public IReadOnlyList<PrivacySessionWire> Sessions { get; init; } = Array.Empty<PrivacySessionWire>();
}

public sealed record AppHistoryPointWire
{
    public long T { get; init; }
    public double Avg { get; init; }
}

public sealed record AppHistoryEntryWire
{
    public string Name { get; init; } = "";
    public long? StartedAtMs { get; init; }
    public double Avg { get; init; }
    public double Max { get; init; }
    /// <summary>Window-average dedicated VRAM in MB; only populated for a
    /// gpu:&lt;gid&gt; series.</summary>
    public double? VramAvgMb { get; init; }
    public IReadOnlyList<AppHistoryPointWire> Points { get; init; } = Array.Empty<AppHistoryPointWire>();
}

public sealed record AppUsageHistoryResponse
{
    public bool Supported { get; init; } = true;
    public IReadOnlyList<AppHistoryEntryWire> Apps { get; init; } = Array.Empty<AppHistoryEntryWire>();
}
