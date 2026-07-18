using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Nexus.Service.Monitoring.History.Binary;

/// <summary>One minute's sum/count/max for every scalar field, in real
/// (already unscaled) units - the in-memory shape RebuildMinute writes from
/// and QueryDecimated folds several of together into a wider decimation
/// step.</summary>
internal readonly record struct ScalarMinuteAgg(
    FieldAgg Cpu, FieldAgg Mem, FieldAgg NetIn, FieldAgg NetOut, FieldAgg CpuTemp, FieldAgg DiskRead, FieldAgg DiskWrite);

/// <summary>
/// The scalar minute-rollup tier (the binary equivalent of
/// SqliteMetricsHistoryStore's metric_minutes table): a RingFile keyed by
/// minute-index (see MinuteTier), one slot per minute holding sum/count/max
/// for cpu/mem/net-in/net-out/cpu-temp/disk-read/disk-write.
/// BinaryMetricsHistoryStore rebuilds a touched minute's slot from
/// ScalarRingStore's raw per-second data on every Append - never
/// accumulates - so a replayed second (a backward clock step re-flushing a ts
/// already committed) is naturally deduped exactly the way
/// SqliteMetricsHistoryStore.UpsertScalarRollup's own INSERT OR REPLACE
/// rebuild is.
///
/// A rebuild's raw data comes from ScalarRingStore.Query, which is itself
/// floor-filtered (see RingFile.PruneFloorSec) - so a minute rebuilt after
/// its own prune floor has advanced past part of it reflects only the
/// readings still above that floor, potentially fewer than a rebuild
/// performed earlier would have shown. This can only happen for a minute
/// backdated below the current floor (a backward-clock replay into
/// already-pruned territory), which none of the pinned rollup specs
/// exercise; SqliteMetricsHistoryStore does not have this asymmetry since it
/// physically deletes pruned rows rather than hiding them behind a read-time
/// floor.
///
/// Net/disk sum/max stay whole int64 (matching ScalarRingStore's own
/// net/disk fields); cpu/mem/cpu-temp sum stays a plain i32 (a
/// 60-reading-per-minute x10 sum tops out at 60000, nowhere near overflowing
/// i32) and their max reuses FixedPointCodec's x10 i16 codec, the same one a
/// single second's own reading uses. Sum/max fields carry no null sentinel of
/// their own: Cnt==0 already means "no reading this minute" (FieldAgg's own
/// convention), so a sum/max value stored alongside a zero count is never
/// read back.
/// </summary>
internal sealed class ScalarMinuteRollupRing : IDisposable
{
    private const int CpuSumOffset = 0;
    private const int CpuCntOffset = CpuSumOffset + sizeof(int);
    private const int CpuMaxOffset = CpuCntOffset + sizeof(int);
    private const int MemSumOffset = CpuMaxOffset + sizeof(short);
    private const int MemCntOffset = MemSumOffset + sizeof(int);
    private const int MemMaxOffset = MemCntOffset + sizeof(int);
    private const int NetInSumOffset = MemMaxOffset + sizeof(short);
    private const int NetInCntOffset = NetInSumOffset + sizeof(long);
    private const int NetInMaxOffset = NetInCntOffset + sizeof(int);
    private const int NetOutSumOffset = NetInMaxOffset + sizeof(long);
    private const int NetOutCntOffset = NetOutSumOffset + sizeof(long);
    private const int NetOutMaxOffset = NetOutCntOffset + sizeof(int);
    private const int CpuTempSumOffset = NetOutMaxOffset + sizeof(long);
    private const int CpuTempCntOffset = CpuTempSumOffset + sizeof(int);
    private const int CpuTempMaxOffset = CpuTempCntOffset + sizeof(int);
    private const int DiskReadSumOffset = CpuTempMaxOffset + sizeof(short);
    private const int DiskReadCntOffset = DiskReadSumOffset + sizeof(long);
    private const int DiskReadMaxOffset = DiskReadCntOffset + sizeof(int);
    private const int DiskWriteSumOffset = DiskReadMaxOffset + sizeof(long);
    private const int DiskWriteCntOffset = DiskWriteSumOffset + sizeof(long);
    private const int DiskWriteMaxOffset = DiskWriteCntOffset + sizeof(int);
    private const int BodyLength = DiskWriteMaxOffset + sizeof(long);

    private readonly RingFile _ring;

    public ScalarMinuteRollupRing(string path, long minuteCapacity, long initialPruneFloorSec)
    {
        _ring = RingFile.CreateOrOpen(path, minuteCapacity, BodyLength, MinuteTier.ToPruneFloorIndex(initialPruneFloorSec));
    }

    public bool RaisePruneFloor(long cutoffSec)
    {
        var floorIndex = MinuteTier.ToPruneFloorIndex(cutoffSec);
        if (floorIndex <= _ring.PruneFloorSec)
        {
            return false;
        }
        _ring.PruneFloorSec = floorIndex;
        return true;
    }

    /// <summary>Rewrites minuteFloorSec's slot from scratch. Always writes,
    /// even when every field's Cnt is zero - the slot's existence (this
    /// minute was touched by at least one Append) is itself the signal
    /// QueryDecimated's rollup-eligible path reads, matching
    /// SqliteMetricsHistoryStore's own unconditional per-touched-minute
    /// upsert.</summary>
    public void RebuildMinute(long minuteFloorSec, ScalarMinuteAgg agg)
    {
        Span<byte> body = stackalloc byte[BodyLength];
        Encode(agg, body);
        _ring.WriteSlot(MinuteTier.ToIndex(minuteFloorSec), body);
    }

    public void Flush() => _ring.Flush();

    /// <summary>Folds every minute slot overlapping [fromSec, toSec] into
    /// stepSeconds-wide output slots (SUM-of-sums / SUM-of-counts /
    /// MAX-of-maxes across however many minutes land in the same output
    /// slot), matching QueryScalarsDecimatedFromRollup's SQL aggregation.
    /// stepSeconds is assumed rollup-eligible (a whole multiple of 60) -
    /// BinaryMetricsHistoryStore only calls this once IsRollupEligible has
    /// already routed here.</summary>
    public IReadOnlyList<ScalarDecimatedSlot> QueryDecimated(long fromSec, long toSec, int stepSeconds)
    {
        var result = new List<ScalarDecimatedSlot>();
        if (toSec < fromSec)
        {
            return result;
        }

        var firstMinuteIndex = MinuteTier.ToIndex(MinuteTier.FloorToMinuteSec(fromSec));
        var lastMinuteIndex = MinuteTier.ToIndex(MinuteTier.FloorToMinuteSec(toSec));

        var bySlot = new SortedDictionary<long, ScalarMinuteAgg>();
        foreach (var (minuteIndex, body) in RingFileScan.Enumerate(_ring, firstMinuteIndex, lastMinuteIndex))
        {
            var minuteFloorSec = minuteIndex * MinuteTier.SecondsPerMinute;
            var slot = minuteFloorSec / stepSeconds * stepSeconds;
            var decoded = Decode(body);
            bySlot[slot] = bySlot.TryGetValue(slot, out var acc) ? Combine(acc, decoded) : decoded;
        }

        foreach (var (slot, agg) in bySlot)
        {
            result.Add(new ScalarDecimatedSlot(
                slot,
                agg.Cpu.Avg, agg.Cpu.MaxOrNull,
                agg.Mem.Avg, agg.Mem.MaxOrNull,
                agg.NetIn.Avg, agg.NetIn.MaxOrNull,
                agg.NetOut.Avg, agg.NetOut.MaxOrNull,
                agg.CpuTemp.Avg, agg.CpuTemp.MaxOrNull,
                agg.DiskRead.Avg, agg.DiskRead.MaxOrNull,
                agg.DiskWrite.Avg, agg.DiskWrite.MaxOrNull));
        }
        return result;
    }

    private static ScalarMinuteAgg Combine(ScalarMinuteAgg a, ScalarMinuteAgg b) => new(
        a.Cpu.Combine(b.Cpu), a.Mem.Combine(b.Mem), a.NetIn.Combine(b.NetIn), a.NetOut.Combine(b.NetOut), a.CpuTemp.Combine(b.CpuTemp),
        a.DiskRead.Combine(b.DiskRead), a.DiskWrite.Combine(b.DiskWrite));

    private static void Encode(ScalarMinuteAgg agg, Span<byte> body)
    {
        BinaryPrimitives.WriteInt32LittleEndian(body[CpuSumOffset..], ScaleSumX10(agg.Cpu.Sum));
        BinaryPrimitives.WriteInt32LittleEndian(body[CpuCntOffset..], agg.Cpu.Cnt);
        BinaryPrimitives.WriteInt16LittleEndian(body[CpuMaxOffset..], FixedPointCodec.ScaleX10(agg.Cpu.MaxOrNull));

        BinaryPrimitives.WriteInt32LittleEndian(body[MemSumOffset..], ScaleSumX10(agg.Mem.Sum));
        BinaryPrimitives.WriteInt32LittleEndian(body[MemCntOffset..], agg.Mem.Cnt);
        BinaryPrimitives.WriteInt16LittleEndian(body[MemMaxOffset..], FixedPointCodec.ScaleX10(agg.Mem.MaxOrNull));

        BinaryPrimitives.WriteInt64LittleEndian(body[NetInSumOffset..], ScaleSumWhole(agg.NetIn.Sum));
        BinaryPrimitives.WriteInt32LittleEndian(body[NetInCntOffset..], agg.NetIn.Cnt);
        BinaryPrimitives.WriteInt64LittleEndian(body[NetInMaxOffset..], ScaleSumWhole(agg.NetIn.MaxOrNull ?? 0));

        BinaryPrimitives.WriteInt64LittleEndian(body[NetOutSumOffset..], ScaleSumWhole(agg.NetOut.Sum));
        BinaryPrimitives.WriteInt32LittleEndian(body[NetOutCntOffset..], agg.NetOut.Cnt);
        BinaryPrimitives.WriteInt64LittleEndian(body[NetOutMaxOffset..], ScaleSumWhole(agg.NetOut.MaxOrNull ?? 0));

        BinaryPrimitives.WriteInt32LittleEndian(body[CpuTempSumOffset..], ScaleSumX10(agg.CpuTemp.Sum));
        BinaryPrimitives.WriteInt32LittleEndian(body[CpuTempCntOffset..], agg.CpuTemp.Cnt);
        BinaryPrimitives.WriteInt16LittleEndian(body[CpuTempMaxOffset..], FixedPointCodec.ScaleX10(agg.CpuTemp.MaxOrNull));

        BinaryPrimitives.WriteInt64LittleEndian(body[DiskReadSumOffset..], ScaleSumWhole(agg.DiskRead.Sum));
        BinaryPrimitives.WriteInt32LittleEndian(body[DiskReadCntOffset..], agg.DiskRead.Cnt);
        BinaryPrimitives.WriteInt64LittleEndian(body[DiskReadMaxOffset..], ScaleSumWhole(agg.DiskRead.MaxOrNull ?? 0));

        BinaryPrimitives.WriteInt64LittleEndian(body[DiskWriteSumOffset..], ScaleSumWhole(agg.DiskWrite.Sum));
        BinaryPrimitives.WriteInt32LittleEndian(body[DiskWriteCntOffset..], agg.DiskWrite.Cnt);
        BinaryPrimitives.WriteInt64LittleEndian(body[DiskWriteMaxOffset..], ScaleSumWhole(agg.DiskWrite.MaxOrNull ?? 0));
    }

    private static ScalarMinuteAgg Decode(ReadOnlySpan<byte> body)
    {
        var cpu = new FieldAgg(
            BinaryPrimitives.ReadInt32LittleEndian(body[CpuSumOffset..]) / 10.0,
            BinaryPrimitives.ReadInt32LittleEndian(body[CpuCntOffset..]),
            FixedPointCodec.UnscaleX10(BinaryPrimitives.ReadInt16LittleEndian(body[CpuMaxOffset..])) ?? 0);
        var mem = new FieldAgg(
            BinaryPrimitives.ReadInt32LittleEndian(body[MemSumOffset..]) / 10.0,
            BinaryPrimitives.ReadInt32LittleEndian(body[MemCntOffset..]),
            FixedPointCodec.UnscaleX10(BinaryPrimitives.ReadInt16LittleEndian(body[MemMaxOffset..])) ?? 0);
        var netIn = new FieldAgg(
            BinaryPrimitives.ReadInt64LittleEndian(body[NetInSumOffset..]),
            BinaryPrimitives.ReadInt32LittleEndian(body[NetInCntOffset..]),
            BinaryPrimitives.ReadInt64LittleEndian(body[NetInMaxOffset..]));
        var netOut = new FieldAgg(
            BinaryPrimitives.ReadInt64LittleEndian(body[NetOutSumOffset..]),
            BinaryPrimitives.ReadInt32LittleEndian(body[NetOutCntOffset..]),
            BinaryPrimitives.ReadInt64LittleEndian(body[NetOutMaxOffset..]));
        var cpuTemp = new FieldAgg(
            BinaryPrimitives.ReadInt32LittleEndian(body[CpuTempSumOffset..]) / 10.0,
            BinaryPrimitives.ReadInt32LittleEndian(body[CpuTempCntOffset..]),
            FixedPointCodec.UnscaleX10(BinaryPrimitives.ReadInt16LittleEndian(body[CpuTempMaxOffset..])) ?? 0);
        var diskRead = new FieldAgg(
            BinaryPrimitives.ReadInt64LittleEndian(body[DiskReadSumOffset..]),
            BinaryPrimitives.ReadInt32LittleEndian(body[DiskReadCntOffset..]),
            BinaryPrimitives.ReadInt64LittleEndian(body[DiskReadMaxOffset..]));
        var diskWrite = new FieldAgg(
            BinaryPrimitives.ReadInt64LittleEndian(body[DiskWriteSumOffset..]),
            BinaryPrimitives.ReadInt32LittleEndian(body[DiskWriteCntOffset..]),
            BinaryPrimitives.ReadInt64LittleEndian(body[DiskWriteMaxOffset..]));
        return new ScalarMinuteAgg(cpu, mem, netIn, netOut, cpuTemp, diskRead, diskWrite);
    }

    // Mirrors ScalarRingStore's own whole-int clamp (a single second's net
    // reading is already clamped to this range before it ever reaches a
    // minute sum, so only the sum-of-up-to-60 side can push past it).
    private static long ScaleSumWhole(double value)
    {
        if (!double.IsFinite(value))
        {
            return 0;
        }
        var scaled = Math.Round(value);
        if (scaled <= long.MinValue)
        {
            return long.MinValue + 1;
        }
        if (scaled >= long.MaxValue)
        {
            return long.MaxValue;
        }
        return (long)scaled;
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

    public void Dispose() => _ring.Dispose();
}
