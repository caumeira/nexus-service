using System.Collections.Generic;
using System.Linq;

namespace Nexus.Service.Monitoring.History;

/// <summary>
/// Locked in-RAM tail of not-yet-flushed AppUsageTick rows. Mirrors
/// MetricsSampleBuffer's discipline (ts-keyed dedup, RemoveThrough after a
/// committed flush, a failure retention cap) on the same flush cadence, but
/// stays a separate small class rather than a shared generic: the two hold
/// different record shapes and MetricsSampleBuffer's public surface is
/// referenced directly by the route layer, so widening it into a generic
/// container would ripple well past this feature.
/// </summary>
public sealed class AppSampleBuffer
{
    // Bounds unflushed growth when the store is failing, mirroring
    // MetricsSampleBuffer.FailureRetentionCapSeconds.
    private const int FailureRetentionCapSeconds = 600;

    private readonly object _lock = new();
    private readonly SortedDictionary<long, AppUsageTick> _ticks = new();

    /// <summary>Buffers a tick; a second Append at the same TsSec replaces
    /// rather than duplicates.</summary>
    public void Append(AppUsageTick tick)
    {
        lock (_lock)
        {
            _ticks[tick.TsSec] = tick;
        }
    }

    /// <summary>Every buffered tick, oldest first.</summary>
    public IReadOnlyList<AppUsageTick> PendingSnapshot()
    {
        lock (_lock)
        {
            return _ticks.Values.ToList();
        }
    }

    /// <summary>Buffered ticks with ts in [fromSec, toSec], oldest first.</summary>
    public IReadOnlyList<AppUsageTick> SnapshotRange(long fromSec, long toSec)
    {
        lock (_lock)
        {
            return _ticks.Values.Where(t => t.TsSec >= fromSec && t.TsSec <= toSec).ToList();
        }
    }

    /// <summary>Drops every buffered tick with ts &lt;= throughSec. Called
    /// after a successful store commit.</summary>
    public void RemoveThrough(long throughSec)
    {
        lock (_lock)
        {
            var stale = _ticks.Keys.Where(ts => ts <= throughSec).ToList();
            foreach (var ts in stale)
            {
                _ticks.Remove(ts);
            }
        }
    }

    /// <summary>Called instead of RemoveThrough when a flush fails: caps
    /// unflushed growth to FailureRetentionCapSeconds by dropping the
    /// oldest entries.</summary>
    public void TrimToRetentionCap(long nowSec)
    {
        lock (_lock)
        {
            var floor = nowSec - FailureRetentionCapSeconds;
            var stale = _ticks.Keys.Where(ts => ts < floor).ToList();
            foreach (var ts in stale)
            {
                _ticks.Remove(ts);
            }
        }
    }
}
