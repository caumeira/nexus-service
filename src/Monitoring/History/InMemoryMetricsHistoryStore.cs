using System.Collections.Generic;
using System.Linq;

namespace Nexus.Service.Monitoring.History;

/// <summary>
/// Non-persistent fallback used when the SQLite file can't be opened, and in
/// unit tests. Self-bounds to a rolling ~3h window on every Append rather
/// than relying on the hourly pruneCutoffSec cadence SqliteMetricsHistoryStore
/// needs to avoid a write burst every tick - an in-memory ring always keeps
/// the newest RingWindowSeconds regardless of that cadence.
/// </summary>
public sealed class InMemoryMetricsHistoryStore : IMetricsHistoryStore
{
    private const long RingWindowSeconds = 3 * 60 * 60;

    private readonly object _lock = new();
    private readonly SortedDictionary<long, MetricSample> _rows = new();

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

    public void Dispose() { }
}
