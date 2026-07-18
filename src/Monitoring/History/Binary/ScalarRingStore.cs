using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace Nexus.Service.Monitoring.History.Binary;

/// <summary>One second's scalar reading reconstructed from ScalarRingStore -
/// the same nullable/x10 semantics MetricSample's scalar fields use (null =
/// that source failed this tick), without the per-GPU/per-fan/per-component
/// lists later phases add on top of a separate ring.</summary>
internal readonly record struct ScalarReading(
    long TsSec,
    double? CpuPercent,
    double? MemoryPercent,
    long? NetInBytesPerSec,
    long? NetOutBytesPerSec,
    double? CpuTempC,
    long? DiskReadBytesPerSec,
    long? DiskWriteBytesPerSec);

/// <summary>
/// The 1Hz scalar ring (cpu/mem/net-in/net-out/cpu-temp/disk-read/disk-write):
/// a RingFile whose body layout and scale/unscale rules this class owns, so
/// RingFile itself never needs to know what a "scalar" is. Percent/temperature
/// fields are stored x10 fixed-point in an i16 (one decimal place of
/// precision, the wire contract's granularity for these fields); net/disk bps
/// fields are i64, not the i32 the original binary-store design sketched, so
/// a link or drive at or above roughly 17 Gbps stays representable instead of
/// wrapping. Each field type has its own reserved "null" sentinel (the type's
/// MinValue), so a source-failed reading is never confused with a genuine
/// zero.
/// </summary>
internal sealed class ScalarRingStore : IDisposable
{
    // Body layout (BodyLength bytes total). i64 fields first (largest
    // alignment) then the three x10 i16 fields - order only matters for
    // computing these offsets once; RingFile treats the whole span as
    // opaque.
    private const int NetInOffset = 0;
    private const int NetOutOffset = NetInOffset + sizeof(long);
    private const int DiskReadOffset = NetOutOffset + sizeof(long);
    private const int DiskWriteOffset = DiskReadOffset + sizeof(long);
    private const int CpuOffset = DiskWriteOffset + sizeof(long);
    private const int MemOffset = CpuOffset + sizeof(short);
    private const int CpuTempOffset = MemOffset + sizeof(short);
    public const int BodyLength = CpuTempOffset + sizeof(short);

    private const short NullX10 = short.MinValue;
    private const long NullWhole = long.MinValue;

    private readonly RingFile _ring;

    public ScalarRingStore(string path, long capacity, long initialPruneFloorSec)
    {
        _ring = RingFile.CreateOrOpen(path, capacity, BodyLength, initialPruneFloorSec);
    }

    /// <summary>Exposed for tests that need a ring small enough to exercise
    /// wraparound and whole-ring-scan behavior directly.</summary>
    internal long Capacity => _ring.Capacity;

    public long PruneFloorSec => _ring.PruneFloorSec;

    /// <summary>Raises the prune floor to <paramref name="cutoffSec"/> if it
    /// is higher than the current floor, and reports whether it changed. The
    /// floor is raised, never lowered, so a backward clock step can never
    /// reopen a window this store already told a caller was gone.</summary>
    public bool RaisePruneFloor(long cutoffSec)
    {
        if (cutoffSec <= _ring.PruneFloorSec)
        {
            return false;
        }
        _ring.PruneFloorSec = cutoffSec;
        return true;
    }

    public void Append(IReadOnlyList<MetricSample> samples)
    {
        Span<byte> body = stackalloc byte[BodyLength];
        foreach (var s in samples)
        {
            Encode(s, body);
            _ring.WriteSlot(s.TsSec, body);
        }
        _ring.Flush();
    }

    /// <summary>Reconstructs every reading with ts in [fromSec, toSec],
    /// ascending by ts - a gap (a ts nothing was ever written for, or one
    /// the prune floor now hides) has no entry at all, distinct from an
    /// entry whose fields are individually null.</summary>
    public IReadOnlyList<ScalarReading> Query(long fromSec, long toSec)
    {
        var result = new List<ScalarReading>();
        if (toSec < fromSec)
        {
            return result;
        }

        Span<byte> body = stackalloc byte[BodyLength];
        if (RangeExceedsCapacity(fromSec, toSec, _ring.Capacity))
        {
            // A window this wide necessarily revisits some physical slots
            // more than once if walked second-by-second (capacity many
            // slots exist, period), so scan every slot once by its raw
            // index instead - bounded work regardless of how wide the
            // caller's window is, including Query(0, long.MaxValue) on an
            // empty ring.
            for (long index = 0; index < _ring.Capacity; index++)
            {
                if (_ring.TryReadSlotAtIndex(index, body, out var ts) && ts >= fromSec && ts <= toSec)
                {
                    result.Add(Decode(ts, body));
                }
            }
            result.Sort((a, b) => a.TsSec.CompareTo(b.TsSec));
        }
        else
        {
            for (var ts = fromSec; ts <= toSec; ts++)
            {
                if (_ring.TryReadSlot(ts, body))
                {
                    result.Add(Decode(ts, body));
                }
            }
        }
        return result;
    }

    // Whether iterating every second in [fromSec, toSec] would touch more
    // slots than the ring even has - true also on any arithmetic edge case
    // (e.g. toSec == long.MaxValue), where falling back to the always-safe
    // whole-ring scan below is strictly better than risking an overflowed
    // comparison.
    private static bool RangeExceedsCapacity(long fromSec, long toSec, long capacity)
    {
        try
        {
            checked
            {
                return toSec - fromSec + 1 >= capacity;
            }
        }
        catch (OverflowException)
        {
            return true;
        }
    }

    /// <summary>The raw (step &lt; 60) decimation path: reads every matching
    /// second via Query, then folds each field through MetricsDecimation
    /// independently and unions the slot keys, matching
    /// InMemoryMetricsHistoryStore.QueryScalarsDecimated byte for byte in
    /// behavior - a slot appears iff at least one field had a non-null
    /// reading in it, and a field within an appearing slot is null iff every
    /// reading of that field in the slot was null.</summary>
    public IReadOnlyList<ScalarDecimatedSlot> QueryScalarsDecimatedRaw(long fromSec, long toSec, int stepSeconds)
    {
        var rows = Query(fromSec, toSec);

        var cpu = Slots(rows, r => r.CpuPercent, fromSec, toSec, stepSeconds);
        var mem = Slots(rows, r => r.MemoryPercent, fromSec, toSec, stepSeconds);
        var netIn = Slots(rows, r => r.NetInBytesPerSec, fromSec, toSec, stepSeconds);
        var netOut = Slots(rows, r => r.NetOutBytesPerSec, fromSec, toSec, stepSeconds);
        var temp = Slots(rows, r => r.CpuTempC, fromSec, toSec, stepSeconds);
        var diskRead = Slots(rows, r => r.DiskReadBytesPerSec, fromSec, toSec, stepSeconds);
        var diskWrite = Slots(rows, r => r.DiskWriteBytesPerSec, fromSec, toSec, stepSeconds);

        var allSlots = new SortedSet<long>();
        allSlots.UnionWith(cpu.Keys);
        allSlots.UnionWith(mem.Keys);
        allSlots.UnionWith(netIn.Keys);
        allSlots.UnionWith(netOut.Keys);
        allSlots.UnionWith(temp.Keys);
        allSlots.UnionWith(diskRead.Keys);
        allSlots.UnionWith(diskWrite.Keys);

        var result = new List<ScalarDecimatedSlot>(allSlots.Count);
        foreach (var slot in allSlots)
        {
            result.Add(new ScalarDecimatedSlot(
                slot,
                cpu.GetValueOrDefault(slot)?.Avg, cpu.GetValueOrDefault(slot)?.Max,
                mem.GetValueOrDefault(slot)?.Avg, mem.GetValueOrDefault(slot)?.Max,
                netIn.GetValueOrDefault(slot)?.Avg, netIn.GetValueOrDefault(slot)?.Max,
                netOut.GetValueOrDefault(slot)?.Avg, netOut.GetValueOrDefault(slot)?.Max,
                temp.GetValueOrDefault(slot)?.Avg, temp.GetValueOrDefault(slot)?.Max,
                diskRead.GetValueOrDefault(slot)?.Avg, diskRead.GetValueOrDefault(slot)?.Max,
                diskWrite.GetValueOrDefault(slot)?.Avg, diskWrite.GetValueOrDefault(slot)?.Max));
        }
        return result;
    }

    private static Dictionary<long, MetricPoint> Slots(
        IReadOnlyList<ScalarReading> rows, Func<ScalarReading, double?> selector, long fromSec, long toSec, int stepSeconds) =>
        MetricsDecimation.Decimate(rows.Select(r => new MetricSamplePoint(r.TsSec, selector(r))), fromSec, toSec, stepSeconds)
            .ToDictionary(p => p.T);

    private static void Encode(MetricSample s, Span<byte> body)
    {
        BinaryPrimitives.WriteInt64LittleEndian(body[NetInOffset..], ScaleWhole(s.NetInBytesPerSec));
        BinaryPrimitives.WriteInt64LittleEndian(body[NetOutOffset..], ScaleWhole(s.NetOutBytesPerSec));
        BinaryPrimitives.WriteInt64LittleEndian(body[DiskReadOffset..], ScaleWhole(s.DiskReadBytesPerSec));
        BinaryPrimitives.WriteInt64LittleEndian(body[DiskWriteOffset..], ScaleWhole(s.DiskWriteBytesPerSec));
        BinaryPrimitives.WriteInt16LittleEndian(body[CpuOffset..], ScaleX10(s.CpuPercent));
        BinaryPrimitives.WriteInt16LittleEndian(body[MemOffset..], ScaleX10(s.MemoryPercent));
        BinaryPrimitives.WriteInt16LittleEndian(body[CpuTempOffset..], ScaleX10(s.CpuTempC));
    }

    private static ScalarReading Decode(long ts, ReadOnlySpan<byte> body)
    {
        var netIn = BinaryPrimitives.ReadInt64LittleEndian(body[NetInOffset..]);
        var netOut = BinaryPrimitives.ReadInt64LittleEndian(body[NetOutOffset..]);
        var diskRead = BinaryPrimitives.ReadInt64LittleEndian(body[DiskReadOffset..]);
        var diskWrite = BinaryPrimitives.ReadInt64LittleEndian(body[DiskWriteOffset..]);
        var cpu = BinaryPrimitives.ReadInt16LittleEndian(body[CpuOffset..]);
        var mem = BinaryPrimitives.ReadInt16LittleEndian(body[MemOffset..]);
        var cpuTemp = BinaryPrimitives.ReadInt16LittleEndian(body[CpuTempOffset..]);
        return new ScalarReading(
            ts,
            UnscaleX10(cpu), UnscaleX10(mem),
            UnscaleWhole(netIn), UnscaleWhole(netOut),
            UnscaleX10(cpuTemp),
            UnscaleWhole(diskRead), UnscaleWhole(diskWrite));
    }

    // Non-finite (NaN/Infinity) is treated the same as a missing reading
    // rather than let a bad sensor value survive the cast below with an
    // implementation-defined result.
    private static short ScaleX10(double? value)
    {
        if (value is not { } v || !double.IsFinite(v))
        {
            return NullX10;
        }
        var scaled = Math.Round(v * 10);
        if (scaled <= short.MinValue)
        {
            return short.MinValue + 1; // MinValue itself is reserved for null
        }
        if (scaled >= short.MaxValue)
        {
            return short.MaxValue;
        }
        return (short)scaled;
    }

    private static double? UnscaleX10(short raw) => raw == NullX10 ? null : raw / 10.0;

    private static long ScaleWhole(double? value)
    {
        if (value is not { } v || !double.IsFinite(v))
        {
            return NullWhole;
        }
        var scaled = Math.Round(v);
        if (scaled <= long.MinValue)
        {
            return long.MinValue + 1; // MinValue itself is reserved for null
        }
        if (scaled >= long.MaxValue)
        {
            return long.MaxValue;
        }
        return (long)scaled;
    }

    private static long? UnscaleWhole(long raw) => raw == NullWhole ? null : raw;

    public void Dispose() => _ring.Dispose();
}
