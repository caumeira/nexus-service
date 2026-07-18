using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Nexus.Service.Monitoring.History.Binary;

/// <summary>
/// Per-storage-drive/RAM-DIMM temperature history: a TempComponentRegistry
/// mapping component id -&gt; ring index (see that class's doc for why
/// storage/RAM need a Kind-carrying registry rather than
/// EntityRegistry), plus one per-entity RingFile pair (raw per-second, minute
/// rollup) per registered component, grown on demand and capped at
/// entityCapacity - the same shape GpuRingStore/FanRingStore use for their
/// own per-entity rings, including the Volatile-swapped array publish
/// discipline (see GpuRingStore's class doc for why a concurrent reader must
/// bound its loop by the ring array's own length, not EntityRegistry.Count).
///
/// The minute rollup exists so QueryComponentTempDecimated's wide-window
/// (step&gt;=60) path can fold minute slots instead of scanning raw
/// per-second rows across a multi-day scrub - the same trade GpuRingStore/
/// FanRingStore already made. The 90-day temperature history diagnostics
/// needs is unrelated: that comes from TempBucketStore, which rebuilds its
/// own 5-minute buckets by reading this store's raw per-second data (see
/// BinaryMetricsHistoryStore.RebuildTempBuckets) regardless of this rollup.
/// </summary>
internal sealed class TempComponentRingStore : IDisposable
{
    private const int ValueOffset = 0;
    private const int SecondBodyLength = ValueOffset + sizeof(short);

    private const int ValueSumOffset = 0;
    private const int ValueCntOffset = ValueSumOffset + sizeof(int);
    private const int ValueMaxOffset = ValueCntOffset + sizeof(int);
    private const int MinuteBodyLength = ValueMaxOffset + sizeof(short);

    private readonly string _dir;
    private readonly long _secondCapacity;
    private readonly long _minuteCapacity;
    private readonly TempComponentRegistry _registry;
    private RingFile[] _secondRings = Array.Empty<RingFile>();
    private RingFile[] _minuteRings = Array.Empty<RingFile>();
    private long _pruneFloorSec;

    public TempComponentRingStore(string dir, long secondCapacity, long minuteCapacity, int entityCapacity, long initialPruneFloorSec)
    {
        Directory.CreateDirectory(dir);
        _dir = dir;
        _secondCapacity = secondCapacity;
        _minuteCapacity = minuteCapacity;
        _pruneFloorSec = initialPruneFloorSec;
        _registry = TempComponentRegistry.Open(Path.Combine(dir, "entities.reg"), entityCapacity);

        if (_registry.Count > 0)
        {
            EnsureRingsExist(_registry.Count - 1);
        }
    }

    // The only writer of _secondRings/_minuteRings (Append's single-writer
    // caller) - see GpuRingStore.EnsureRingsExist for the publish ordering
    // this mirrors.
    private void EnsureRingsExist(int upToIndexInclusive)
    {
        var second = _secondRings;
        var minute = _minuteRings;
        while (second.Length <= upToIndexInclusive)
        {
            var i = second.Length;
            var nextSecond = new RingFile[second.Length + 1];
            Array.Copy(second, nextSecond, second.Length);
            nextSecond[i] = RingFile.CreateOrOpen(
                Path.Combine(_dir, $"{i}.ring"), _secondCapacity, SecondBodyLength, _pruneFloorSec);
            second = nextSecond;

            var nextMinute = new RingFile[minute.Length + 1];
            Array.Copy(minute, nextMinute, minute.Length);
            nextMinute[i] = RingFile.CreateOrOpen(
                Path.Combine(_dir, $"{i}.min"), _minuteCapacity, MinuteBodyLength, MinuteTier.ToPruneFloorIndex(_pruneFloorSec));
            minute = nextMinute;
        }
        Volatile.Write(ref _minuteRings, minute);
        Volatile.Write(ref _secondRings, second);
    }

    public void Append(IReadOnlyList<MetricSample> samples)
    {
        var touchedMinutesByIndex = new Dictionary<int, HashSet<long>>();
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

                if (!touchedMinutesByIndex.TryGetValue(idx, out var minutes))
                {
                    minutes = new HashSet<long>();
                    touchedMinutesByIndex[idx] = minutes;
                }
                minutes.Add(MinuteTier.FloorToMinuteSec(s.TsSec));
            }
        }

        foreach (var (idx, minutes) in touchedMinutesByIndex)
        {
            foreach (var minute in minutes)
            {
                RebuildMinute(idx, minute);
            }
        }

        foreach (var ring in _secondRings)
        {
            ring.Flush();
        }
        foreach (var ring in _minuteRings)
        {
            ring.Flush();
        }
    }

    private void RebuildMinute(int idx, long minuteFloorSec)
    {
        var raw = RingFileScan.Enumerate(_secondRings[idx], minuteFloorSec, minuteFloorSec + 59);
        var value = FieldAgg.FromReadings(raw.Select(r => FixedPointCodec.UnscaleX10(BinaryPrimitives.ReadInt16LittleEndian(r.Body.AsSpan(ValueOffset)))));

        Span<byte> body = stackalloc byte[MinuteBodyLength];
        EncodeMinute(value, body);
        _minuteRings[idx].WriteSlot(MinuteTier.ToIndex(minuteFloorSec), body);
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
        var minuteFloorIndex = MinuteTier.ToPruneFloorIndex(cutoffSec);
        foreach (var ring in Volatile.Read(ref _minuteRings))
        {
            if (minuteFloorIndex > ring.PruneFloorSec)
            {
                ring.PruneFloorSec = minuteFloorIndex;
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

    /// <summary>The raw (step&lt;60) path: per-component avg/max within a
    /// slot - see GpuRingStore.QueryRawDecimated for the row-existence
    /// inclusion rule this mirrors (a slot appears for a component iff at
    /// least one ts row existed for it in that slot's range, regardless of
    /// whether the value itself is null that row).</summary>
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

    /// <summary>The rollup-eligible (step&gt;=60) path: folds every minute
    /// slot overlapping [fromSec, toSec] into stepSeconds-wide output slots
    /// per component, one output row per minute-that-existed - see
    /// GpuRingStore.QueryRollupDecimated for the row-per-touched-minute
    /// grouping this mirrors.</summary>
    public IReadOnlyList<ComponentTempDecimatedSlot> QueryRollupDecimated(long fromSec, long toSec, int stepSeconds)
    {
        var result = new List<ComponentTempDecimatedSlot>();
        if (toSec < fromSec)
        {
            return result;
        }

        var firstMinuteIndex = MinuteTier.ToIndex(MinuteTier.FloorToMinuteSec(fromSec));
        var lastMinuteIndex = MinuteTier.ToIndex(MinuteTier.FloorToMinuteSec(toSec));

        var minuteRings = Volatile.Read(ref _minuteRings);
        var entries = _registry.Entries;
        for (var idx = 0; idx < minuteRings.Length; idx++)
        {
            var (id, kind, name) = entries[idx];
            var bySlot = new SortedDictionary<long, FieldAgg>();

            foreach (var (minuteIndex, body) in RingFileScan.Enumerate(minuteRings[idx], firstMinuteIndex, lastMinuteIndex))
            {
                var minuteFloorSec = minuteIndex * MinuteTier.SecondsPerMinute;
                var slot = minuteFloorSec / stepSeconds * stepSeconds;
                var value = DecodeMinute(body);
                bySlot[slot] = bySlot.TryGetValue(slot, out var acc) ? acc.Combine(value) : value;
            }

            foreach (var (slot, agg) in bySlot)
            {
                result.Add(new ComponentTempDecimatedSlot(id, kind, name, slot, agg.Avg, agg.MaxOrNull));
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

    private static void EncodeMinute(FieldAgg value, Span<byte> body)
    {
        BinaryPrimitives.WriteInt32LittleEndian(body[ValueSumOffset..], ScaleSumX10(value.Sum));
        BinaryPrimitives.WriteInt32LittleEndian(body[ValueCntOffset..], value.Cnt);
        BinaryPrimitives.WriteInt16LittleEndian(body[ValueMaxOffset..], FixedPointCodec.ScaleX10(value.MaxOrNull));
    }

    private static FieldAgg DecodeMinute(ReadOnlySpan<byte> body) =>
        new(
            BinaryPrimitives.ReadInt32LittleEndian(body[ValueSumOffset..]) / 10.0,
            BinaryPrimitives.ReadInt32LittleEndian(body[ValueCntOffset..]),
            FixedPointCodec.UnscaleX10(BinaryPrimitives.ReadInt16LittleEndian(body[ValueMaxOffset..])) ?? 0);

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
        foreach (var ring in _secondRings)
        {
            ring.Dispose();
        }
        foreach (var ring in _minuteRings)
        {
            ring.Dispose();
        }
        _registry.Dispose();
    }
}
