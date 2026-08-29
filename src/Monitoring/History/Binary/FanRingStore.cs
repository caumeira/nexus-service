using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Nexus.Service.Monitoring.History.Binary;

/// <summary>
/// Per-fan RPM/duty history - the same shape as GpuRingStore (an
/// EntityRegistry plus a grown-on-demand,
/// capped-at-entityCapacity per-entity RingFile pair, published through
/// Volatile-swapped arrays so a concurrent reader never bounds its loop by a
/// count a ring array hasn't caught up to yet - see GpuRingStore's class
/// doc), with rpm/duty stored as plain whole ints
/// (FixedPointCodec.ScaleInt16/UnscaleInt16) rather than gpu's x10 fixed
/// point - fan readings are already whole numbers on the wire
/// (FanReading.Rpm/Duty are int?), so scaling them would only lose precision
/// for no benefit.
/// </summary>
internal sealed class FanRingStore : IDisposable
{
    private const int RpmOffset = 0;
    private const int DutyOffset = RpmOffset + sizeof(short);
    private const int SecondBodyLength = DutyOffset + sizeof(short);

    private const int RpmSumOffset = 0;
    private const int RpmCntOffset = RpmSumOffset + sizeof(int);
    private const int RpmMaxOffset = RpmCntOffset + sizeof(int);
    private const int DutySumOffset = RpmMaxOffset + sizeof(short);
    private const int DutyCntOffset = DutySumOffset + sizeof(int);
    private const int DutyMaxOffset = DutyCntOffset + sizeof(int);
    private const int MinuteBodyLength = DutyMaxOffset + sizeof(short);

    private readonly string _dir;
    private readonly long _secondCapacity;
    private readonly long _minuteCapacity;
    private readonly EntityRegistry _registry;
    private RingFile[] _secondRings = Array.Empty<RingFile>();
    private RingFile[] _minuteRings = Array.Empty<RingFile>();
    private long _pruneFloorSec;

    public FanRingStore(string dir, long secondCapacity, long minuteCapacity, int entityCapacity, long initialPruneFloorSec)
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
            foreach (var f in s.Fans)
            {
                var index = _registry.RegisterOrGet(f.FanId, f.Name);
                if (index is not { } idx)
                {
                    continue;
                }
                EnsureRingsExist(idx);

                BinaryPrimitives.WriteInt16LittleEndian(body[RpmOffset..], FixedPointCodec.ScaleInt16(f.Rpm));
                BinaryPrimitives.WriteInt16LittleEndian(body[DutyOffset..], FixedPointCodec.ScaleInt16(f.Duty));
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
        var rpm = FieldAgg.FromReadings(raw.Select(r => (double?)FixedPointCodec.UnscaleInt16(BinaryPrimitives.ReadInt16LittleEndian(r.Body.AsSpan(RpmOffset)))));
        var duty = FieldAgg.FromReadings(raw.Select(r => (double?)FixedPointCodec.UnscaleInt16(BinaryPrimitives.ReadInt16LittleEndian(r.Body.AsSpan(DutyOffset)))));

        Span<byte> body = stackalloc byte[MinuteBodyLength];
        EncodeMinute(rpm, duty, body);
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

    /// <summary>Every fan reading in [fromSec, toSec], keyed by ts - see
    /// GpuRingStore.Query for the row-existence-driven inclusion rule this
    /// mirrors.</summary>
    public Dictionary<long, List<FanReading>> Query(long fromSec, long toSec)
    {
        var result = new Dictionary<long, List<FanReading>>();
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
                var rpm = FixedPointCodec.UnscaleInt16(BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(RpmOffset)));
                var duty = FixedPointCodec.UnscaleInt16(BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(DutyOffset)));
                if (!result.TryGetValue(ts, out var list))
                {
                    list = new List<FanReading>();
                    result[ts] = list;
                }
                list.Add(new FanReading(id, name, rpm, duty));
            }
        }
        return result;
    }

    /// <summary>The raw (step&lt;60) path - see GpuRingStore.QueryRawDecimated
    /// for the row-existence inclusion rule this mirrors (a slot appears for
    /// a fan iff at least one ts row existed for it in that slot's range,
    /// regardless of whether rpm/duty themselves are null that row).</summary>
    public IReadOnlyList<FanDecimatedSlot> QueryRawDecimated(long fromSec, long toSec, int stepSeconds)
    {
        var result = new List<FanDecimatedSlot>();
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

            var rpm = Slots(rows, RpmOffset, fromSec, toSec, stepSeconds);
            var duty = Slots(rows, DutyOffset, fromSec, toSec, stepSeconds);

            var slots = new SortedSet<long>(rows.Select(r => r.Key / stepSeconds * stepSeconds));
            foreach (var slot in slots)
            {
                result.Add(new FanDecimatedSlot(
                    id, name, slot,
                    rpm.GetValueOrDefault(slot)?.Avg, rpm.GetValueOrDefault(slot)?.Max,
                    duty.GetValueOrDefault(slot)?.Avg, duty.GetValueOrDefault(slot)?.Max));
            }
        }
        return result;
    }

    /// <summary>The rollup-eligible (step&gt;=60) path - see
    /// GpuRingStore.QueryRollupDecimated for the row-per-touched-minute
    /// grouping this mirrors.</summary>
    public IReadOnlyList<FanDecimatedSlot> QueryRollupDecimated(long fromSec, long toSec, int stepSeconds)
    {
        var result = new List<FanDecimatedSlot>();
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
            var bySlot = new SortedDictionary<long, (FieldAgg Rpm, FieldAgg Duty)>();

            foreach (var (minuteIndex, body) in RingFileScan.Enumerate(minuteRings[idx], firstMinuteIndex, lastMinuteIndex))
            {
                var minuteFloorSec = minuteIndex * MinuteTier.SecondsPerMinute;
                var slot = minuteFloorSec / stepSeconds * stepSeconds;
                var (rpm, duty) = DecodeMinute(body);
                bySlot[slot] = bySlot.TryGetValue(slot, out var acc)
                    ? (acc.Rpm.Combine(rpm), acc.Duty.Combine(duty))
                    : (rpm, duty);
            }

            foreach (var (slot, agg) in bySlot)
            {
                result.Add(new FanDecimatedSlot(id, name, slot, agg.Rpm.Avg, agg.Rpm.MaxOrNull, agg.Duty.Avg, agg.Duty.MaxOrNull));
            }
        }
        return result;
    }

    private static Dictionary<long, MetricPoint> Slots(
        IReadOnlyList<(long Ts, byte[] Body)> rows, int fieldOffset, long fromSec, long toSec, int stepSeconds) =>
        MetricsDecimation.Decimate(
                rows.Select(r => new MetricSamplePoint(r.Ts, (double?)FixedPointCodec.UnscaleInt16(BinaryPrimitives.ReadInt16LittleEndian(r.Body.AsSpan(fieldOffset))))),
                fromSec, toSec, stepSeconds)
            .ToDictionary(p => p.T);

    private static void EncodeMinute(FieldAgg rpm, FieldAgg duty, Span<byte> body)
    {
        BinaryPrimitives.WriteInt32LittleEndian(body[RpmSumOffset..], ScaleSumWhole(rpm.Sum));
        BinaryPrimitives.WriteInt32LittleEndian(body[RpmCntOffset..], rpm.Cnt);
        BinaryPrimitives.WriteInt16LittleEndian(body[RpmMaxOffset..], FixedPointCodec.ScaleInt16(ToInt(rpm.MaxOrNull)));

        BinaryPrimitives.WriteInt32LittleEndian(body[DutySumOffset..], ScaleSumWhole(duty.Sum));
        BinaryPrimitives.WriteInt32LittleEndian(body[DutyCntOffset..], duty.Cnt);
        BinaryPrimitives.WriteInt16LittleEndian(body[DutyMaxOffset..], FixedPointCodec.ScaleInt16(ToInt(duty.MaxOrNull)));
    }

    private static (FieldAgg Rpm, FieldAgg Duty) DecodeMinute(ReadOnlySpan<byte> body)
    {
        var rpm = new FieldAgg(
            BinaryPrimitives.ReadInt32LittleEndian(body[RpmSumOffset..]),
            BinaryPrimitives.ReadInt32LittleEndian(body[RpmCntOffset..]),
            FixedPointCodec.UnscaleInt16(BinaryPrimitives.ReadInt16LittleEndian(body[RpmMaxOffset..])) ?? 0);
        var duty = new FieldAgg(
            BinaryPrimitives.ReadInt32LittleEndian(body[DutySumOffset..]),
            BinaryPrimitives.ReadInt32LittleEndian(body[DutyCntOffset..]),
            FixedPointCodec.UnscaleInt16(BinaryPrimitives.ReadInt16LittleEndian(body[DutyMaxOffset..])) ?? 0);
        return (rpm, duty);
    }

    private static int? ToInt(double? value) => value is { } v ? (int)Math.Round(v) : null;

    // A minute's rpm/duty sum (up to 60 readings) fits comfortably in i32 at
    // any realistic fan speed or duty percentage; clamped defensively rather
    // than left to wrap on an unchecked cast.
    private static int ScaleSumWhole(double value)
    {
        if (!double.IsFinite(value))
        {
            return 0;
        }
        var scaled = Math.Round(value);
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

    /// <summary>Reformats every registered entity's second and minute rings
    /// to empty; entity registration itself is untouched. Returns the number
    /// of ring files cleared.</summary>
    public int Clear()
    {
        foreach (var ring in _secondRings)
        {
            ring.Clear();
        }
        foreach (var ring in _minuteRings)
        {
            ring.Clear();
        }
        return _secondRings.Length + _minuteRings.Length;
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
