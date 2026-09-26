using System;
using System.Collections.Concurrent;

namespace Nexus.Service.Widgets;

/// <summary>
/// Per-app sliding-window rate limit on <c>PUT /apps-api/data/{appId}/{key}</c>,
/// same bucket shape as <see cref="AppDispatchRateLimiter"/> but capped lower -
/// app-data writes hit disk on every call, where dispatch mostly does not.
/// </summary>
public sealed class AppDataWriteRateLimiter
{
    private const int MaxPerWindow = 10;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);

    public bool TryAcquire(string appId)
    {
        var bucket = _buckets.GetOrAdd(appId, static _ => new Bucket());
        lock (bucket)
        {
            var now = DateTime.UtcNow;
            if (now - bucket.WindowStart >= Window)
            {
                bucket.WindowStart = now;
                bucket.Count = 0;
            }
            if (bucket.Count >= MaxPerWindow) return false;
            bucket.Count++;
            return true;
        }
    }

    private sealed class Bucket
    {
        public DateTime WindowStart = DateTime.UtcNow;
        public int Count;
    }
}
