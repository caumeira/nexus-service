using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Nexus.Service.Monitoring.History.Binary;

/// <summary>
/// The unified 90-day temperature bucket rollup (the binary equivalent of
/// SqliteMetricsHistoryStore's temp_buckets table): a TempComponentRegistry
/// mapping a component key ("cpu", "gpu:&lt;id&gt;", or a storage/ram
/// ComponentId as-is) to a ring index, plus one bucket-width RingFile per
/// registered key holding sum/count/max - the same shape GpuRingStore's
/// minute ring uses, just at TempBucketTier's wider width and
/// MetricsHistory.TempRetentionDays' longer capacity.
///
/// This store has no notion of cpu/gpu/temp-component rings itself - the
/// facade (BinaryMetricsHistoryStore.RebuildTempBuckets) reads those on every
/// Append and calls RebuildBucket per touched (key, bucket) pair, mirroring
/// SqliteMetricsHistoryStore.UpsertTempBucketsRollup's cross-table rebuild
/// and its source-retention guard (a bucket whose raw data has aged past
/// _sourceFloorSec is never passed to RebuildBucket at all). RebuildBucket
/// itself silently skips a zero-reading rebuild rather than writing an empty
/// bucket, matching UpsertTempBucketsRollup's HAVING COUNT(...) &gt; 0 guard -
/// a bucket already written by an earlier, more complete rebuild is never
/// overwritten by a later one that finds nothing.
///
/// Unlike SqliteMetricsHistoryStore's cpu bucket row (whose name column is
/// overwritten on every rebuild with that batch's live CpuName),
/// TempComponentRegistry fixes a key's name at first registration - the same
/// first-seen-wins simplification EntityRegistry/TempComponentRegistry
/// already make elsewhere. No pinned test exercises a CPU rename mid-session
/// (it doesn't, in practice), so this only differs from SQLite in a scenario
/// nothing here relies on.
/// </summary>
internal sealed class TempBucketStore : IDisposable
{
    private const int SumOffset = 0;
    private const int CntOffset = SumOffset + sizeof(int);
    private const int MaxOffset = CntOffset + sizeof(int);
    private const int BodyLength = MaxOffset + sizeof(short);

    private readonly string _dir;
    private readonly long _bucketCapacity;
    private readonly TempComponentRegistry _registry;
    private RingFile[] _rings = Array.Empty<RingFile>();
    private long _pruneFloorSec;

    public TempBucketStore(string dir, long bucketCapacity, int entityCapacity, long initialPruneFloorSec)
    {
        Directory.CreateDirectory(dir);
        _dir = dir;
        _bucketCapacity = bucketCapacity;
        _pruneFloorSec = initialPruneFloorSec;
        _registry = TempComponentRegistry.Open(Path.Combine(dir, "entities.reg"), entityCapacity);

        if (_registry.Count > 0)
        {
            EnsureRingsExist(_registry.Count - 1);
        }
    }

    private void EnsureRingsExist(int upToIndexInclusive)
    {
        var rings = _rings;
        while (rings.Length <= upToIndexInclusive)
        {
            var i = rings.Length;
            var next = new RingFile[rings.Length + 1];
            Array.Copy(rings, next, rings.Length);
            next[i] = RingFile.CreateOrOpen(
                Path.Combine(_dir, $"{i}.ring"), _bucketCapacity, BodyLength, TempBucketTier.ToPruneFloorIndex(_pruneFloorSec));
            rings = next;
        }
        Volatile.Write(ref _rings, rings);
    }

    /// <summary>Rewrites the (id, bucketFloorSec) slot from agg, or leaves
    /// any existing slot untouched if agg has no readings (Cnt==0).</summary>
    public void RebuildBucket(string id, string kind, string name, long bucketFloorSec, FieldAgg agg)
    {
        if (agg.Cnt == 0)
        {
            return;
        }
        var index = _registry.RegisterOrGet(id, kind, name);
        if (index is not { } idx)
        {
            return;
        }
        EnsureRingsExist(idx);

        Span<byte> body = stackalloc byte[BodyLength];
        Encode(agg, body);
        _rings[idx].WriteSlot(TempBucketTier.ToIndex(bucketFloorSec), body);
    }

    public bool RaisePruneFloor(long cutoffSec)
    {
        var changed = false;
        if (cutoffSec > _pruneFloorSec)
        {
            _pruneFloorSec = cutoffSec;
        }
        var floorIndex = TempBucketTier.ToPruneFloorIndex(cutoffSec);
        foreach (var ring in Volatile.Read(ref _rings))
        {
            if (floorIndex > ring.PruneFloorSec)
            {
                ring.PruneFloorSec = floorIndex;
                changed = true;
            }
        }
        return changed;
    }

    public void Flush()
    {
        foreach (var ring in _rings)
        {
            ring.Flush();
        }
    }

    /// <summary>Every bucket whose start falls in [fromSec, toSec] - a strict
    /// containment test, unlike the minute rollup's overlap-inclusive window
    /// (matches SqliteMetricsHistoryStore.QueryTemperatureBuckets' bucket_ts
    /// BETWEEN from AND to exactly), across every registered key.</summary>
    public IReadOnlyList<TemperatureBucketRow> Query(long fromSec, long toSec)
    {
        var result = new List<TemperatureBucketRow>();
        if (toSec < fromSec)
        {
            return result;
        }

        var firstIndex = TempBucketTier.ToPruneFloorIndex(fromSec);
        var lastIndex = TempBucketTier.ToIndex(TempBucketTier.FloorToBucketSec(toSec));
        if (lastIndex < firstIndex)
        {
            return result;
        }

        var rings = Volatile.Read(ref _rings);
        var entries = _registry.Entries;
        for (var idx = 0; idx < rings.Length; idx++)
        {
            var (id, kind, name) = entries[idx];
            foreach (var (bucketIndex, body) in RingFileScan.Enumerate(rings[idx], firstIndex, lastIndex))
            {
                var agg = Decode(body);
                result.Add(new TemperatureBucketRow(
                    id, kind, name, bucketIndex * TempBucketTier.SecondsPerBucket * 1000L,
                    agg.Avg!.Value, agg.MaxOrNull!.Value, agg.Cnt));
            }
        }
        return result;
    }

    private static void Encode(FieldAgg agg, Span<byte> body)
    {
        BinaryPrimitives.WriteInt32LittleEndian(body[SumOffset..], ScaleSumX10(agg.Sum));
        BinaryPrimitives.WriteInt32LittleEndian(body[CntOffset..], agg.Cnt);
        BinaryPrimitives.WriteInt16LittleEndian(body[MaxOffset..], FixedPointCodec.ScaleX10(agg.MaxOrNull));
    }

    private static FieldAgg Decode(ReadOnlySpan<byte> body)
    {
        var sumX10 = BinaryPrimitives.ReadInt32LittleEndian(body[SumOffset..]);
        var cnt = BinaryPrimitives.ReadInt32LittleEndian(body[CntOffset..]);
        var maxX10 = BinaryPrimitives.ReadInt16LittleEndian(body[MaxOffset..]);
        return new FieldAgg(sumX10 / 10.0, cnt, FixedPointCodec.UnscaleX10(maxX10) ?? 0);
    }

    // A bucket's sum of up to TempBucketTier.SecondsPerBucket x10-scaled
    // readings stays comfortably inside i32 at any realistic temperature;
    // clamped defensively rather than left to wrap on an unchecked cast.
    private static int ScaleSumX10(double value)
    {
        if (!double.IsFinite(value))
        {
            return 0;
        }
        var scaled = Math.Round(value * 10);
        if (scaled <= int.MinValue)
        {
            return int.MinValue + 1;
        }
        if (scaled >= int.MaxValue)
        {
            return int.MaxValue;
        }
        return (int)scaled;
    }

    public void Dispose()
    {
        foreach (var ring in _rings)
        {
            ring.Dispose();
        }
        _registry.Dispose();
    }
}
