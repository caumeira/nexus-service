using System.Collections.Generic;
using System.Linq;

namespace Nexus.Service.Monitoring.History;

/// <summary>
/// Non-persistent fallback used when the SQLite file can't be opened, and in
/// unit tests. Self-bounds to a rolling RingWindowSeconds window on every
/// Append rather than relying on the hourly pruneCutoffSec cadence
/// SqliteMetricsHistoryStore needs to avoid a write burst every tick - an
/// in-memory ring always keeps its window regardless of that cadence. Also
/// backs privacy sessions for the same fallback reason.
/// </summary>
public sealed class InMemoryMetricsHistoryStore : IMetricsHistoryStore, IPrivacySessionStore
{
    private const long RingWindowSeconds = 3 * 60 * 60;

    private readonly object _lock = new();
    private readonly SortedDictionary<long, MetricSample> _rows = new();
    private readonly Dictionary<(string AppId, string Capability, long StartUtcSec), PrivacySession> _privacySessions = new();

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

    public void Dispose() { }
}
