using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Nexus.Service.Monitoring.History.Binary;

/// <summary>
/// Per-storage-drive/RAM-DIMM temperature history (the binary equivalent of
/// SqliteMetricsHistoryStore's temp_component_series/temp_component_seconds):
/// a TempComponentRegistry mapping component id -&gt; ring index (see that
/// class's doc for why storage/RAM need a Kind-carrying registry rather than
/// EntityRegistry), plus one per-second RingFile per registered component,
/// grown on demand and capped at entityCapacity - the same shape
/// GpuRingStore/FanRingStore use for their own second-ring tier, including
/// the Volatile-swapped array publish discipline (see GpuRingStore's class
/// doc for why a concurrent reader must bound its loop by the ring array's
/// own length, not EntityRegistry.Count).
///
/// Unlike GpuRingStore/FanRingStore, there is no minute-rollup tier here:
/// SqliteMetricsHistoryStore never built a temp_component_minutes table (see
/// its class doc), since QueryComponentTempDecimated always aggregates the
/// raw per-second table directly - this store matches that. The 90-day
/// temperature history diagnostics needs instead comes from TempBucketStore,
/// which rebuilds its own 5-minute buckets by reading this store's raw data
/// (see BinaryMetricsHistoryStore.RebuildTempBuckets).
/// </summary>
internal sealed class TempComponentRingStore : IDisposable
{
    private const int ValueOffset = 0;
    private const int SecondBodyLength = ValueOffset + sizeof(short);

    private readonly string _dir;
    private readonly long _secondCapacity;
    private readonly TempComponentRegistry _registry;
    private RingFile[] _secondRings = Array.Empty<RingFile>();
    private long _pruneFloorSec;

    public TempComponentRingStore(string dir, long secondCapacity, int entityCapacity, long initialPruneFloorSec)
    {
        Directory.CreateDirectory(dir);
        _dir = dir;
        _secondCapacity = secondCapacity;
        _pruneFloorSec = initialPruneFloorSec;
        _registry = TempComponentRegistry.Open(Path.Combine(dir, "entities.reg"), entityCapacity);

        if (_registry.Count > 0)
        {
            EnsureRingsExist(_registry.Count - 1);
        }
    }

    // The only writer of _secondRings (Append's single-writer caller) - see
    // GpuRingStore.EnsureRingsExist for the publish ordering this mirrors.
    private void EnsureRingsExist(int upToIndexInclusive)
    {
        var second = _secondRings;
        while (second.Length <= upToIndexInclusive)
        {
            var i = second.Length;
            var next = new RingFile[second.Length + 1];
            Array.Copy(second, next, second.Length);
            next[i] = RingFile.CreateOrOpen(
                Path.Combine(_dir, $"{i}.ring"), _secondCapacity, SecondBodyLength, _pruneFloorSec);
            second = next;
        }
        Volatile.Write(ref _secondRings, second);
    }

    public void Append(IReadOnlyList<MetricSample> samples)
    {
        Span<byte> body = stackalloc byte[SecondBodyLength];
        foreach (var s in samples)
        {
            foreach (var c in s.ComponentTemps)
            {
                var index = _registry.RegisterOrGet(c.ComponentId, c.Kind, c.Name);
                if (index is not { } idx)
                {
                    continue;
                }
                EnsureRingsExist(idx);

                BinaryPrimitives.WriteInt16LittleEndian(body[ValueOffset..], FixedPointCodec.ScaleX10(c.ValueC));
                _secondRings[idx].WriteSlot(s.TsSec, body);
            }
        }

        foreach (var ring in _secondRings)
        {
            ring.Flush();
        }
    }

    public bool RaisePruneFloor(long cutoffSec)
    {
        var changed = false;
        if (cutoffSec > _pruneFloorSec)
        {
            _pruneFloorSec = cutoffSec;
        }
        foreach (var ring in Volatile.Read(ref _secondRings))
        {
            if (cutoffSec > ring.PruneFloorSec)
            {
                ring.PruneFloorSec = cutoffSec;
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>Every component temperature reading in [fromSec, toSec],
    /// keyed by ts - see GpuRingStore.Query for the row-existence-driven
    /// inclusion rule this mirrors.</summary>
    public Dictionary<long, List<ComponentTempReading>> Query(long fromSec, long toSec)
    {
        var result = new Dictionary<long, List<ComponentTempReading>>();
        if (toSec < fromSec)
        {
            return result;
        }

        var secondRings = Volatile.Read(ref _secondRings);
        var entries = _registry.Entries;
        for (var idx = 0; idx < secondRings.Length; idx++)
        {
            var (id, kind, name) = entries[idx];
            foreach (var (ts, body) in RingFileScan.Enumerate(secondRings[idx], fromSec, toSec))
            {
                var value = FixedPointCodec.UnscaleX10(BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(ValueOffset)));
                if (!result.TryGetValue(ts, out var list))
                {
                    list = new List<ComponentTempReading>();
                    result[ts] = list;
                }
                list.Add(new ComponentTempReading(id, kind, name, value));
            }
        }
        return result;
    }

    /// <summary>The only QueryComponentTempDecimated path - see
    /// GpuRingStore.QueryRawDecimated for the row-existence inclusion rule
    /// this mirrors (a slot appears for a component iff at least one ts row
    /// existed for it in that slot's range, regardless of whether the value
    /// itself is null that row).</summary>
    public IReadOnlyList<ComponentTempDecimatedSlot> QueryRawDecimated(long fromSec, long toSec, int stepSeconds)
    {
        var result = new List<ComponentTempDecimatedSlot>();
        if (toSec < fromSec)
        {
            return result;
        }

        var secondRings = Volatile.Read(ref _secondRings);
        var entries = _registry.Entries;
        for (var idx = 0; idx < secondRings.Length; idx++)
        {
            var (id, kind, name) = entries[idx];
            var rows = RingFileScan.Enumerate(secondRings[idx], fromSec, toSec);
            if (rows.Count == 0)
            {
                continue;
            }

            var values = Slots(rows, fromSec, toSec, stepSeconds);
            var slots = new SortedSet<long>(rows.Select(r => r.Key / stepSeconds * stepSeconds));
            foreach (var slot in slots)
            {
                result.Add(new ComponentTempDecimatedSlot(
                    id, kind, name, slot,
                    values.GetValueOrDefault(slot)?.Avg, values.GetValueOrDefault(slot)?.Max));
            }
        }
        return result;
    }

    private static Dictionary<long, MetricPoint> Slots(
        IReadOnlyList<(long Key, byte[] Body)> rows, long fromSec, long toSec, int stepSeconds) =>
        MetricsDecimation.Decimate(
                rows.Select(r => new MetricSamplePoint(r.Key, FixedPointCodec.UnscaleX10(BinaryPrimitives.ReadInt16LittleEndian(r.Body.AsSpan(ValueOffset))))),
                fromSec, toSec, stepSeconds)
            .ToDictionary(p => p.T);

    public void Dispose()
    {
        foreach (var ring in _secondRings)
        {
            ring.Dispose();
        }
        _registry.Dispose();
    }
}
