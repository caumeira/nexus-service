using System.Collections.Generic;
using System.Linq;

namespace Nexus.Service.Monitoring.History;

/// <summary>
/// Non-persistent fallback used when the SQLite file can't be opened, and in
/// unit tests. Self-bounds to a rolling RingWindowSeconds window on every
/// Append rather than relying on the hourly pruneCutoffSec cadence
/// SqliteMetricsHistoryStore needs to avoid a write burst every tick - an
/// in-memory ring always keeps its window regardless of that cadence. Also
/// backs privacy sessions and per-app usage history for the same fallback
/// reason.
/// </summary>
public sealed class InMemoryMetricsHistoryStore : IMetricsHistoryStore, IPrivacySessionStore, IAppUsageHistoryStore
{
    private const long RingWindowSeconds = 3 * 60 * 60;

    private readonly object _lock = new();
    private readonly SortedDictionary<long, MetricSample> _rows = new();
    private readonly Dictionary<(string AppId, string Capability, long StartUtcSec), PrivacySession> _privacySessions = new();
    private readonly SortedDictionary<long, AppUsageTick> _appTicks = new();

    public void Append(IReadOnlyList<MetricSample> samples, long? pruneCutoffSec)
    {
        lock (_lock)
        {
            foreach (var s in samples)
            {
                _rows[s.TsSec] = s;
            }

            if (_rows.Count == 0)
            {
                return;
            }

            var newestTs = _rows.Keys.Last();
            var ringFloor = newestTs - RingWindowSeconds;
            var floor = pruneCutoffSec is { } cutoff ? System.Math.Max(cutoff, ringFloor) : ringFloor;

            var stale = _rows.Keys.Where(ts => ts < floor).ToList();
            foreach (var ts in stale)
            {
                _rows.Remove(ts);
            }
        }
    }

    public IReadOnlyList<MetricSample> Query(long fromSec, long toSec)
    {
        lock (_lock)
        {
            return _rows.Values.Where(s => s.TsSec >= fromSec && s.TsSec <= toSec).ToList();
        }
    }

    // The ring holds at most RingWindowSeconds of raw MetricSample rows, so
    // decimating in C# here (reusing the same MetricsDecimation the SQLite
    // store's callers use for the RAM tail) costs nothing worth avoiding -
    // no SQL-side aggregation to fall back to on this fallback store.
    public IReadOnlyList<ScalarDecimatedSlot> QueryScalarsDecimated(long fromSec, long toSec, int stepSeconds)
    {
        lock (_lock)
        {
            var rows = _rows.Values.Where(s => s.TsSec >= fromSec && s.TsSec <= toSec).ToList();
            var cpu = Slots(rows, s => s.CpuPercent, fromSec, toSec, stepSeconds);
            var mem = Slots(rows, s => s.MemoryPercent, fromSec, toSec, stepSeconds);
            var netIn = Slots(rows, s => s.NetInBytesPerSec, fromSec, toSec, stepSeconds);
            var netOut = Slots(rows, s => s.NetOutBytesPerSec, fromSec, toSec, stepSeconds);
            var temp = Slots(rows, s => s.CpuTempC, fromSec, toSec, stepSeconds);

            var allSlots = new SortedSet<long>(cpu.Keys.Concat(mem.Keys).Concat(netIn.Keys).Concat(netOut.Keys).Concat(temp.Keys));
            var result = new List<ScalarDecimatedSlot>(allSlots.Count);
            foreach (var slot in allSlots)
            {
                result.Add(new ScalarDecimatedSlot(
                    slot,
                    cpu.GetValueOrDefault(slot)?.Avg, cpu.GetValueOrDefault(slot)?.Max,
                    mem.GetValueOrDefault(slot)?.Avg, mem.GetValueOrDefault(slot)?.Max,
                    netIn.GetValueOrDefault(slot)?.Avg, netIn.GetValueOrDefault(slot)?.Max,
                    netOut.GetValueOrDefault(slot)?.Avg, netOut.GetValueOrDefault(slot)?.Max,
                    temp.GetValueOrDefault(slot)?.Avg, temp.GetValueOrDefault(slot)?.Max));
            }
            return result;
        }
    }

    public IReadOnlyList<GpuDecimatedSlot> QueryGpuDecimated(long fromSec, long toSec, int stepSeconds)
    {
        lock (_lock)
        {
            var rows = _rows.Values.Where(s => s.TsSec >= fromSec && s.TsSec <= toSec).ToList();
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var s in rows)
            {
                foreach (var g in s.Gpus)
                {
                    names[g.GpuId] = g.Name;
                }
            }

            var result = new List<GpuDecimatedSlot>();
            foreach (var (gpuId, name) in names)
            {
                var load = Slots(rows, s => s.Gpus.FirstOrDefault(g => g.GpuId == gpuId)?.LoadPercent, fromSec, toSec, stepSeconds);
                var temp = Slots(rows, s => s.Gpus.FirstOrDefault(g => g.GpuId == gpuId)?.TempC, fromSec, toSec, stepSeconds);
                foreach (var slot in new SortedSet<long>(load.Keys.Concat(temp.Keys)))
                {
                    result.Add(new GpuDecimatedSlot(
                        gpuId, name, slot,
                        load.GetValueOrDefault(slot)?.Avg, load.GetValueOrDefault(slot)?.Max,
                        temp.GetValueOrDefault(slot)?.Avg, temp.GetValueOrDefault(slot)?.Max));
                }
            }
            return result;
        }
    }

    public IReadOnlyList<FanDecimatedSlot> QueryFanDecimated(long fromSec, long toSec, int stepSeconds)
    {
        lock (_lock)
        {
            var rows = _rows.Values.Where(s => s.TsSec >= fromSec && s.TsSec <= toSec).ToList();
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var s in rows)
            {
                foreach (var f in s.Fans)
                {
                    names[f.FanId] = f.Name;
                }
            }

            var result = new List<FanDecimatedSlot>();
            foreach (var (fanId, name) in names)
            {
                var rpm = Slots(rows, s => (double?)s.Fans.FirstOrDefault(f => f.FanId == fanId)?.Rpm, fromSec, toSec, stepSeconds);
                var duty = Slots(rows, s => (double?)s.Fans.FirstOrDefault(f => f.FanId == fanId)?.Duty, fromSec, toSec, stepSeconds);
                foreach (var slot in new SortedSet<long>(rpm.Keys.Concat(duty.Keys)))
                {
                    result.Add(new FanDecimatedSlot(
                        fanId, name, slot,
                        rpm.GetValueOrDefault(slot)?.Avg, rpm.GetValueOrDefault(slot)?.Max,
                        duty.GetValueOrDefault(slot)?.Avg, duty.GetValueOrDefault(slot)?.Max));
                }
            }
            return result;
        }
    }

    private static Dictionary<long, MetricPoint> Slots(
        IReadOnlyList<MetricSample> rows, Func<MetricSample, double?> selector, long fromSec, long toSec, int stepSeconds) =>
        MetricsDecimation.Decimate(rows.Select(s => new MetricSamplePoint(s.TsSec, selector(s))), fromSec, toSec, stepSeconds)
            .ToDictionary(p => p.T);

    public void Upsert(string capability, string appId, long startUtcSec, long? endUtcSec)
    {
        lock (_lock)
        {
            _privacySessions[(appId, capability, startUtcSec)] = new PrivacySession(appId, capability, startUtcSec, endUtcSec);
        }
    }

    IReadOnlyList<PrivacySession> IPrivacySessionStore.Query(long fromSec, long toSec)
    {
        lock (_lock)
        {
            return _privacySessions.Values
                .Where(s => s.StartUtcSec <= toSec && (s.EndUtcSec is null || s.EndUtcSec >= fromSec))
                .OrderBy(s => s.StartUtcSec)
                .ToList();
        }
    }

    void IPrivacySessionStore.PruneOlderThan(long cutoffSec)
    {
        lock (_lock)
        {
            // An open session (EndUtcSec null) past retention by its start is
            // pruned too - see SqliteMetricsHistoryStore's PruneOlderThan.
            var stale = _privacySessions.Where(kv =>
                    kv.Value.EndUtcSec is { } end ? end < cutoffSec : kv.Value.StartUtcSec < cutoffSec)
                .Select(kv => kv.Key).ToList();
            foreach (var key in stale)
            {
                _privacySessions.Remove(key);
            }
        }
    }

    public void Append(IReadOnlyList<AppUsageTick> ticks, long? pruneCutoffSec)
    {
        lock (_lock)
        {
            foreach (var t in ticks)
            {
                _appTicks[t.TsSec] = t;
            }

            if (_appTicks.Count == 0)
            {
                return;
            }

            var newestTs = _appTicks.Keys.Last();
            var ringFloor = newestTs - RingWindowSeconds;
            var floor = pruneCutoffSec is { } cutoff ? System.Math.Max(cutoff, ringFloor) : ringFloor;

            var stale = _appTicks.Keys.Where(ts => ts < floor).ToList();
            foreach (var ts in stale)
            {
                _appTicks.Remove(ts);
            }
        }
    }

    public IReadOnlyList<AppWindowStat> QueryTopApps(string metric, long fromSec, long toSec, int maxApps)
    {
        lock (_lock)
        {
            // Divides by the ticks the metric was actually sampled in the
            // window (expectedTicks), not by an app's own row count - see
            // SqliteMetricsHistoryStore.QueryTopApps for why.
            var sums = new Dictionary<string, (double Sum, double Max)>(StringComparer.OrdinalIgnoreCase);
            var expectedTicks = 0;
            foreach (var tick in _appTicks.Values)
            {
                if (tick.TsSec < fromSec || tick.TsSec > toSec)
                {
                    continue;
                }
                var m = tick.Metrics.FirstOrDefault(x => x.Metric == metric);
                if (m is null)
                {
                    continue;
                }
                expectedTicks++;
                foreach (var a in m.Apps)
                {
                    var acc = sums.TryGetValue(a.Name, out var v) ? v : (0, double.MinValue);
                    sums[a.Name] = (acc.Sum + a.Value, System.Math.Max(acc.Max, a.Value));
                }
            }
            if (expectedTicks == 0)
            {
                return Array.Empty<AppWindowStat>();
            }

            return sums
                .Select(kv => new AppWindowStat(kv.Key, kv.Value.Sum / expectedTicks, kv.Value.Max))
                .OrderByDescending(s => s.Avg)
                .Take(maxApps)
                .ToList();
        }
    }

    public IReadOnlyList<long> QuerySampledTicks(string metric, long fromSec, long toSec)
    {
        lock (_lock)
        {
            return _appTicks.Values
                .Where(t => t.TsSec >= fromSec && t.TsSec <= toSec && t.Metrics.Any(m => m.Metric == metric))
                .Select(t => t.TsSec)
                .OrderBy(ts => ts)
                .ToList();
        }
    }

    public IReadOnlyList<AppRawPoint> QueryAppSeries(string metric, string appName, long fromSec, long toSec)
    {
        lock (_lock)
        {
            var result = new List<AppRawPoint>();
            foreach (var tick in _appTicks.Values)
            {
                if (tick.TsSec < fromSec || tick.TsSec > toSec)
                {
                    continue;
                }
                foreach (var m in tick.Metrics)
                {
                    if (m.Metric != metric)
                    {
                        continue;
                    }
                    foreach (var a in m.Apps)
                    {
                        if (string.Equals(a.Name, appName, StringComparison.OrdinalIgnoreCase))
                        {
                            result.Add(new AppRawPoint(tick.TsSec, a.Value, a.VramMb));
                        }
                    }
                }
            }
            return result;
        }
    }

    public long? QueryFirstSeen(string appName)
    {
        lock (_lock)
        {
            // _appTicks is a SortedDictionary keyed by TsSec, so the first
            // match in iteration order is already the earliest.
            foreach (var tick in _appTicks.Values)
            {
                foreach (var m in tick.Metrics)
                {
                    if (m.Apps.Any(a => string.Equals(a.Name, appName, StringComparison.OrdinalIgnoreCase)))
                    {
                        return tick.TsSec;
                    }
                }
            }
            return null;
        }
    }

    public void Dispose() { }
}
