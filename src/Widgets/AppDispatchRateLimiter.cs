using System;
using System.Collections.Concurrent;

namespace Nexus.Service.Widgets;

/// <summary>
/// Per-widget sliding-window rate limit on <c>/apps-api/dispatch</c>.
/// Control actions drive real hardware (DDC brightness, fan duty, media
/// transport); a runaway worker loop could hammer them. Each widget is capped
/// to a small number of dispatches per second — generous for interactive use,
/// but a hard ceiling on a flood. The single safety net now that the SDK is the
/// only widget path (the declarative tier is gone).
/// </summary>
public sealed class AppDispatchRateLimiter
{
    private const int MaxPerWindow = 20;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);

    /// <summary>Returns false if this widget has exceeded its dispatch budget
    /// for the current window (the caller should reject with 429).</summary>
    public bool TryAcquire(string widgetId)
    {
        var bucket = _buckets.GetOrAdd(widgetId, static _ => new Bucket());
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
