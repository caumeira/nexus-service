using System.Collections.Generic;
using System.Linq;

namespace Nexus.Service.Diagnostics.Temperature;

/// <summary>
/// Non-persistent fallback used in unit tests and when the SQLite file can't be
/// opened. Keyed by (component_id, bucket_utc) so UpsertBuckets matches the
/// SQLite store's replace-on-conflict semantics.
/// </summary>
public sealed class InMemoryTemperatureHistoryStore : ITemperatureHistoryStore
{
    private readonly object _lock = new();
    private readonly Dictionary<(string ComponentId, long BucketUtcMs), TemperatureBucketRow> _rows = new();

    public void UpsertBuckets(IReadOnlyList<TemperatureBucketRow> rows)
    {
        lock (_lock)
        {
            foreach (var row in rows)
            {
                _rows[(row.ComponentId, row.BucketUtcMs)] = row;
            }
        }
    }

    public IReadOnlyList<TemperatureBucketRow> Query(long fromUtcMs, long toUtcMs)
    {
        lock (_lock)
        {
            return _rows.Values
                .Where(r => r.BucketUtcMs >= fromUtcMs && r.BucketUtcMs <= toUtcMs)
                .OrderBy(r => r.BucketUtcMs)
                .ToList();
        }
    }

    public int PruneOlderThan(long utcMs)
    {
        lock (_lock)
        {
            var stale = _rows.Where(kv => kv.Value.BucketUtcMs < utcMs).Select(kv => kv.Key).ToList();
            foreach (var key in stale)
            {
                _rows.Remove(key);
            }
            return stale.Count;
        }
    }

    public void Dispose() { }
}
