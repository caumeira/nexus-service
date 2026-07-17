using System.Collections.Concurrent;

namespace Nexus.Service.Monitoring.History;

/// <summary>
/// Memoizes GET /monitoring/process-info's firstSeenMs per app name for the
/// life of the service. Retention pruning can push the true earliest sample
/// later over time as older rows age out; re-running the multi-table
/// MIN(ts) scan on every request to track that drift is not worth it for a
/// value only used to say "roughly how long has Nexus seen this app".
/// </summary>
public sealed class ProcessFirstSeenCache
{
    private readonly IAppUsageHistoryStore _store;
    private readonly ConcurrentDictionary<string, long> _cache = new(System.StringComparer.OrdinalIgnoreCase);

    public ProcessFirstSeenCache(IAppUsageHistoryStore store) { _store = store; }

    /// <summary>First-seen epoch milliseconds for appName, or null when the
    /// store has never recorded it.</summary>
    public long? Resolve(string appName)
    {
        if (_cache.TryGetValue(appName, out var cachedMs))
        {
            return cachedMs;
        }

        if (_store.QueryFirstSeen(appName) is not { } firstSeenSec)
        {
            return null;
        }

        var ms = firstSeenSec * 1000;
        _cache[appName] = ms;
        return ms;
    }
}
