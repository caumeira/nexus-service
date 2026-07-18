using System;
using System.Collections.Generic;

namespace Nexus.Service.Monitoring.History.Binary;

/// <summary>
/// The windowed-or-full-scan read strategy every ring in this store uses to
/// answer a [fromSec, toSec] query: walk each key in the range one at a time
/// when the range is narrower than the ring's own capacity, or make one pass
/// over every physical slot index (bounded work regardless of how wide the
/// caller's window is) when it is not - ScalarRingStore.Query established
/// this choice for the raw scalar ring; every ring Phase 2 adds (the scalar
/// minute rollup, and the per-entity gpu/fan second and minute rings) reuses
/// it here rather than re-deriving it once per ring kind.
///
/// Always returns results ordered by key ascending, even though the
/// full-scan branch visits physical slots in index order (which is not
/// chronological once a ring has wrapped) - callers that feed the result
/// into MetricsDecimation.Decimate depend on that ordering, and sorting once
/// here is cheaper to get right than trusting every caller to remember.
/// </summary>
internal static class RingFileScan
{
    public static List<(long Key, byte[] Body)> Enumerate(RingFile ring, long fromKey, long toKey)
    {
        var result = new List<(long Key, byte[] Body)>();
        if (toKey < fromKey)
        {
            return result;
        }

        var scratch = new byte[ring.BodyLength];
        if (RangeExceedsCapacity(fromKey, toKey, ring.Capacity))
        {
            for (long index = 0; index < ring.Capacity; index++)
            {
                if (ring.TryReadSlotAtIndex(index, scratch, out var key) && key >= fromKey && key <= toKey)
                {
                    result.Add((key, (byte[])scratch.Clone()));
                }
            }
            result.Sort((a, b) => a.Key.CompareTo(b.Key));
        }
        else
        {
            for (var key = fromKey; key <= toKey; key++)
            {
                if (ring.TryReadSlot(key, scratch))
                {
                    result.Add((key, (byte[])scratch.Clone()));
                }
            }
        }
        return result;
    }

    // Whether iterating every key in [fromKey, toKey] would touch more slots
    // than the ring even has - true also on any arithmetic edge case (e.g.
    // toKey == long.MaxValue), where the always-safe whole-ring scan above is
    // strictly better than risking an overflowed comparison.
    private static bool RangeExceedsCapacity(long fromKey, long toKey, long capacity)
    {
        try
        {
            checked
            {
                return toKey - fromKey + 1 >= capacity;
            }
        }
        catch (OverflowException)
        {
            return true;
        }
    }
}
