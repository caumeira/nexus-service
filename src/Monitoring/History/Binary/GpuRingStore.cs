using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Nexus.Service.Monitoring.History.Binary;

/// <summary>
/// Per-GPU load/temperature history (the binary equivalent of
/// SqliteMetricsHistoryStore's gpu_series/gpu_seconds/gpu_minutes): an
/// EntityRegistry mapping gpu id -> ring index, plus one RingFile pair
/// (raw per-second, minute rollup) per registered GPU, grown one pair at a
/// time as new GPUs are first seen and capped at entityCapacity - a real
/// desktop never comes close to that cap, so the bound exists only to keep
/// the store's footprint predictable if it somehow did. A GPU beyond the cap
/// is silently not persisted, mirroring EntityRegistry's own overflow rule.
///
/// Unlike a single shared file that grows in whole-ring increments, each
/// GPU's ring pair is its own file (dir/&lt;index&gt;.ring, dir/&lt;index&gt;.min) -
/// reusing RingFile.CreateOrOpen directly per entity rather than teaching
/// RingFile to grow an already-mapped file in place. The on-disk footprint is
/// identical either way (N rings' worth of bytes for N entities); this is N
/// small files instead of one file N rings long.
///
/// The two ring-file arrays publish through Volatile.Write, never mutated in
/// place, the same discipline EntityRegistry's own array uses - Append (the
/// single writer) grows them via EnsureRingsExist; a concurrent reader
/// (Query/QueryRawDecimated/QueryRollupDecimated, called from HTTP route
/// handlers) snapshots the array it is about to index ONCE via Volatile.Read
/// and bounds its loop by that snapshot's own Length, not by
/// EntityRegistry.Count - a ring array can lag one registration behind the
/// registry (RegisterOrGet publishes before EnsureRingsExist runs), so
/// bounding by the registry's count instead could index past the ring
/// array's actual length.
/// </summary>
internal sealed class GpuRingStore : IDisposable
{
    private const int LoadOffset = 0;
    private const int TempOffset = LoadOffset + sizeof(short);
    private const int SecondBodyLength = TempOffset + sizeof(short);

    private const int LoadSumOffset = 0;
    private const int LoadCntOffset = LoadSumOffset + sizeof(int);
    private const int LoadMaxOffset = LoadCntOffset + sizeof(int);
    private const int TempSumOffset = LoadMaxOffset + sizeof(short);
    private const int TempCntOffset = TempSumOffset + sizeof(int);
    private const int TempMaxOffset = TempCntOffset + sizeof(int);
    private const int MinuteBodyLength = TempMaxOffset + sizeof(short);

    private readonly string _dir;
    private readonly long _secondCapacity;
    private readonly long _minuteCapacity;
    private readonly EntityRegistry _registry;
    private RingFile[] _secondRings = Array.Empty<RingFile>();
    private RingFile[] _minuteRings = Array.Empty<RingFile>();
    private long _pruneFloorSec;

    public GpuRingStore(string dir, long secondCapacity, long minuteCapacity, int entityCapacity, long initialPruneFloorSec)
    {
        Directory.CreateDirectory(dir);
        _dir = dir;
        _secondCapacity = secondCapacity;
        _minuteCapacity = minuteCapacity;
        _pruneFloorSec = initialPruneFloorSec;
        _registry = EntityRegistry.Open(Path.Combine(dir, "entities.reg"), entityCapacity);

        if (_registry.Count > 0)
        {
            EnsureRingsExist(_registry.Count - 1);
        }
    }

    // The only writer of _secondRings/_minuteRings (Append's single-writer
    // caller). Builds whole replacement arrays and publishes each with one
    // Volatile.Write - see the class doc for why a reader must never bound
    // its loop by EntityRegistry.Count instead of the array it indexes.
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

    /// <summary>The registered ring index for <paramref name="gpuId"/>, or
    /// null if no scalar sample has ever registered it - the per-app usage
    /// store uses this to resolve a "gpu:&lt;gid&gt;"/"vram:&lt;gid&gt;" metric id
    /// to the same identity the scalar side already tracks, and to skip
    /// recording app rows for a gpu id it has never seen scalar data for.</summary>
    public int? TryGetIndex(string gpuId) => _registry.TryGetIndex(gpuId);

    public void Append(IReadOnlyList<MetricSample> samples)
    {
        var touchedMinutesByIndex = new Dictionary<int, HashSet<long>>();
        Span<byte> body = stackalloc byte[SecondBodyLength];

        foreach (var s in samples)
        {
            foreach (var g in s.Gpus)
            {
                var index = _registry.RegisterOrGet(g.GpuId, g.Name);
                if (index is not { } idx)
                {
                    continue;
                }
                EnsureRingsExist(idx);

                BinaryPrimitives.WriteInt16LittleEndian(body[LoadOffset..], FixedPointCodec.ScaleX10(g.LoadPercent));
                BinaryPrimitives.WriteInt16LittleEndian(body[TempOffset..], FixedPointCodec.ScaleX10(g.TempC));
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
        var load = FieldAgg.FromReadings(raw.Select(r => FixedPointCodec.UnscaleX10(BinaryPrimitives.ReadInt16LittleEndian(r.Body.AsSpan(LoadOffset)))));
        var temp = FieldAgg.FromReadings(raw.Select(r => FixedPointCodec.UnscaleX10(BinaryPrimitives.ReadInt16LittleEndian(r.Body.AsSpan(TempOffset)))));

        Span<byte> body = stackalloc byte[MinuteBodyLength];
        EncodeMinute(load, temp, body);
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

    /// <summary>Every GPU reading in [fromSec, toSec], keyed by ts - a
    /// GpuReading appears for a ts iff that GPU had ANY entry (even an
    /// all-null one) at that ts, matching SqliteMetricsHistoryStore.Query's
    /// row-existence-driven reconstruction.</summary>
    public Dictionary<long, List<GpuReading>> Query(long fromSec, long toSec)
    {
        var result = new Dictionary<long, List<GpuReading>>();
        if (toSec < fromSec)
        {
            return result;
        }

        var secondRings = Volatile.Read(ref _secondRings);
        var entries = _registry.Entries;
        for (var idx = 0; idx < secondRings.Length; idx++)
        {
            var (id, name) = entries[idx];
            foreach (var (ts, body) in RingFileScan.Enumerate(secondRings[idx], fromSec, toSec))
            {
                var load = FixedPointCodec.UnscaleX10(BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(LoadOffset)));
                var temp = FixedPointCodec.UnscaleX10(BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(TempOffset)));
                if (!result.TryGetValue(ts, out var list))
                {
                    list = new List<GpuReading>();
                    result[ts] = list;
                }
                list.Add(new GpuReading(id, name, "", load, temp));
            }
        }
        return result;
    }

    /// <summary>The raw (step&lt;60) path: per-GPU avg/max within a slot. A
    /// slot appears for a GPU iff at least one ts row existed for it in that
    /// slot's range - matching QueryGpuDecimatedFromRaw's SQL GROUP BY over
    /// gpu_seconds, which groups by row existence regardless of whether
    /// load/temp themselves are null that row (a GPU whose sensor lookup
    /// fails for both fields in a whole window still reports empty rows, not
    /// a missing slot).</summary>
    public IReadOnlyList<GpuDecimatedSlot> QueryRawDecimated(long fromSec, long toSec, int stepSeconds)
    {
        var result = new List<GpuDecimatedSlot>();
        if (toSec < fromSec)
        {
            return result;
        }

        var secondRings = Volatile.Read(ref _secondRings);
        var entries = _registry.Entries;
        for (var idx = 0; idx < secondRings.Length; idx++)
        {
            var (id, name) = entries[idx];
            var rows = RingFileScan.Enumerate(secondRings[idx], fromSec, toSec);
            if (rows.Count == 0)
            {
                continue;
            }

            var load = Slots(rows, LoadOffset, fromSec, toSec, stepSeconds);
            var temp = Slots(rows, TempOffset, fromSec, toSec, stepSeconds);

            var slots = new SortedSet<long>(rows.Select(r => r.Key / stepSeconds * stepSeconds));
            foreach (var slot in slots)
            {
                result.Add(new GpuDecimatedSlot(
                    id, name, slot,
                    load.GetValueOrDefault(slot)?.Avg, load.GetValueOrDefault(slot)?.Max,
                    temp.GetValueOrDefault(slot)?.Avg, temp.GetValueOrDefault(slot)?.Max));
            }
        }
        return result;
    }

    /// <summary>The rollup-eligible (step&gt;=60) path: folds every minute
    /// slot overlapping [fromSec, toSec] into stepSeconds-wide output slots
    /// per GPU, one output row per minute-that-existed (matching
    /// QueryGpuDecimatedFromRollup's row-per-touched-minute grouping)
    /// regardless of whether the folded load/temp end up null.</summary>
    public IReadOnlyList<GpuDecimatedSlot> QueryRollupDecimated(long fromSec, long toSec, int stepSeconds)
    {
        var result = new List<GpuDecimatedSlot>();
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
            var (id, name) = entries[idx];
            var bySlot = new SortedDictionary<long, (FieldAgg Load, FieldAgg Temp)>();

            foreach (var (minuteIndex, body) in RingFileScan.Enumerate(minuteRings[idx], firstMinuteIndex, lastMinuteIndex))
            {
                var minuteFloorSec = minuteIndex * MinuteTier.SecondsPerMinute;
                var slot = minuteFloorSec / stepSeconds * stepSeconds;
                var (load, temp) = DecodeMinute(body);
                bySlot[slot] = bySlot.TryGetValue(slot, out var acc)
                    ? (acc.Load.Combine(load), acc.Temp.Combine(temp))
                    : (load, temp);
            }

            foreach (var (slot, agg) in bySlot)
            {
                result.Add(new GpuDecimatedSlot(id, name, slot, agg.Load.Avg, agg.Load.MaxOrNull, agg.Temp.Avg, agg.Temp.MaxOrNull));
            }
        }
        return result;
    }

    private static Dictionary<long, MetricPoint> Slots(
        IReadOnlyList<(long Ts, byte[] Body)> rows, int fieldOffset, long fromSec, long toSec, int stepSeconds) =>
        MetricsDecimation.Decimate(
                rows.Select(r => new MetricSamplePoint(r.Ts, FixedPointCodec.UnscaleX10(BinaryPrimitives.ReadInt16LittleEndian(r.Body.AsSpan(fieldOffset))))),
                fromSec, toSec, stepSeconds)
            .ToDictionary(p => p.T);

    private static void EncodeMinute(FieldAgg load, FieldAgg temp, Span<byte> body)
    {
        BinaryPrimitives.WriteInt32LittleEndian(body[LoadSumOffset..], ScaleSumX10(load.Sum));
        BinaryPrimitives.WriteInt32LittleEndian(body[LoadCntOffset..], load.Cnt);
        BinaryPrimitives.WriteInt16LittleEndian(body[LoadMaxOffset..], FixedPointCodec.ScaleX10(load.MaxOrNull));

        BinaryPrimitives.WriteInt32LittleEndian(body[TempSumOffset..], ScaleSumX10(temp.Sum));
        BinaryPrimitives.WriteInt32LittleEndian(body[TempCntOffset..], temp.Cnt);
        BinaryPrimitives.WriteInt16LittleEndian(body[TempMaxOffset..], FixedPointCodec.ScaleX10(temp.MaxOrNull));
    }

    private static (FieldAgg Load, FieldAgg Temp) DecodeMinute(ReadOnlySpan<byte> body)
    {
        var load = new FieldAgg(
            BinaryPrimitives.ReadInt32LittleEndian(body[LoadSumOffset..]) / 10.0,
            BinaryPrimitives.ReadInt32LittleEndian(body[LoadCntOffset..]),
            FixedPointCodec.UnscaleX10(BinaryPrimitives.ReadInt16LittleEndian(body[LoadMaxOffset..])) ?? 0);
        var temp = new FieldAgg(
            BinaryPrimitives.ReadInt32LittleEndian(body[TempSumOffset..]) / 10.0,
            BinaryPrimitives.ReadInt32LittleEndian(body[TempCntOffset..]),
            FixedPointCodec.UnscaleX10(BinaryPrimitives.ReadInt16LittleEndian(body[TempMaxOffset..])) ?? 0);
        return (load, temp);
    }

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
