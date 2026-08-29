using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace Nexus.Service.Monitoring.History.Binary;

/// <summary>
/// The 1Hz fps ring, kept separate from ScalarRingStore so cpu/mem/net/disk
/// history stays byte-compatible across builds - see ScalarRingStore's class
/// doc. A single i16 body: frames &lt;= 0 (no presenter, or a presenter
/// reporting 0) stores the same null sentinel as a missing reading, per the
/// monitoring series' gap-not-zero contract.
/// </summary>
internal sealed class FpsRingStore : IDisposable
{
    private const int BodyLength = sizeof(short);
    private const short NullFps = short.MinValue;

    private readonly RingFile _ring;
    private readonly object _writeLock = new();

    public FpsRingStore(string path, long capacity, long initialPruneFloorSec)
    {
        _ring = RingFile.CreateOrOpen(path, capacity, BodyLength, initialPruneFloorSec);
    }

    public long PruneFloorSec => _ring.PruneFloorSec;

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
        lock (_writeLock)
        {
            Span<byte> body = stackalloc byte[BodyLength];
            foreach (var s in samples)
            {
                BinaryPrimitives.WriteInt16LittleEndian(body, Scale(s.Fps));
                _ring.WriteSlot(s.TsSec, body);
            }
            _ring.Flush();
        }
    }

    public IReadOnlyList<(long TsSec, int? Fps)> Query(long fromSec, long toSec)
    {
        var result = new List<(long, int?)>();
        if (toSec < fromSec)
        {
            return result;
        }

        Span<byte> body = stackalloc byte[BodyLength];
        if (RangeExceedsCapacity(fromSec, toSec, _ring.Capacity))
        {
            for (long index = 0; index < _ring.Capacity; index++)
            {
                if (_ring.TryReadSlotAtIndex(index, body, out var ts) && ts >= fromSec && ts <= toSec)
                {
                    result.Add((ts, Unscale(BinaryPrimitives.ReadInt16LittleEndian(body))));
                }
            }
            result.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        }
        else
        {
            for (var ts = fromSec; ts <= toSec; ts++)
            {
                if (_ring.TryReadSlot(ts, body))
                {
                    result.Add((ts, Unscale(BinaryPrimitives.ReadInt16LittleEndian(body))));
                }
            }
        }
        return result;
    }

    private static bool RangeExceedsCapacity(long fromSec, long toSec, long capacity)
    {
        try
        {
            checked { return toSec - fromSec + 1 >= capacity; }
        }
        catch (OverflowException)
        {
            return true;
        }
    }

    public IReadOnlyList<(long Slot, double? Avg, double? Max)> QueryDecimatedRaw(long fromSec, long toSec, int stepSeconds)
    {
        var rows = Query(fromSec, toSec);
        var points = MetricsDecimation.Decimate(
            rows.Select(r => new MetricSamplePoint(r.TsSec, (double?)r.Fps)), fromSec, toSec, stepSeconds);
        return points.Select(p => (p.T, (double?)p.Avg, (double?)p.Max)).ToList();
    }

    private static short Scale(int? value)
    {
        if (value is not { } v || v <= 0)
        {
            return NullFps;
        }
        return v >= short.MaxValue ? short.MaxValue : (short)v;
    }

    private static int? Unscale(short raw) => raw == NullFps ? null : raw;

    public void Clear()
    {
        lock (_writeLock)
        {
            _ring.Clear();
        }
    }

    public void Dispose() => _ring.Dispose();
}

/// <summary>Minute rollup for FpsRingStore, the fps counterpart of
/// ScalarMinuteRollupRing over a single FieldAgg field.</summary>
internal sealed class FpsMinuteRollupRing : IDisposable
{
    private const int SumOffset = 0;
    private const int CntOffset = SumOffset + sizeof(int);
    private const int MaxOffset = CntOffset + sizeof(int);
    private const int BodyLength = MaxOffset + sizeof(short);

    private readonly RingFile _ring;

    public FpsMinuteRollupRing(string path, long minuteCapacity, long initialPruneFloorSec)
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

    public void RebuildMinute(long minuteFloorSec, FieldAgg agg)
    {
        Span<byte> body = stackalloc byte[BodyLength];
        Encode(agg, body);
        _ring.WriteSlot(MinuteTier.ToIndex(minuteFloorSec), body);
    }

    public void Flush() => _ring.Flush();

    public IReadOnlyList<(long Slot, double? Avg, double? Max)> QueryDecimated(long fromSec, long toSec, int stepSeconds)
    {
        var result = new List<(long, double?, double?)>();
        if (toSec < fromSec)
        {
            return result;
        }

        var firstMinuteIndex = MinuteTier.ToIndex(MinuteTier.FloorToMinuteSec(fromSec));
        var lastMinuteIndex = MinuteTier.ToIndex(MinuteTier.FloorToMinuteSec(toSec));

        var bySlot = new SortedDictionary<long, FieldAgg>();
        foreach (var (minuteIndex, body) in RingFileScan.Enumerate(_ring, firstMinuteIndex, lastMinuteIndex))
        {
            var minuteFloorSec = minuteIndex * MinuteTier.SecondsPerMinute;
            var slot = minuteFloorSec / stepSeconds * stepSeconds;
            var decoded = Decode(body);
            bySlot[slot] = bySlot.TryGetValue(slot, out var acc) ? acc.Combine(decoded) : decoded;
        }

        foreach (var (slot, agg) in bySlot)
        {
            result.Add((slot, agg.Avg, agg.MaxOrNull));
        }
        return result;
    }

    private static void Encode(FieldAgg agg, Span<byte> body)
    {
        BinaryPrimitives.WriteInt32LittleEndian(body[SumOffset..], (int)Math.Clamp(agg.Sum, int.MinValue, int.MaxValue));
        BinaryPrimitives.WriteInt32LittleEndian(body[CntOffset..], agg.Cnt);
        BinaryPrimitives.WriteInt16LittleEndian(body[MaxOffset..], (short)Math.Clamp(agg.MaxOrNull ?? 0, 0, short.MaxValue));
    }

    private static FieldAgg Decode(ReadOnlySpan<byte> body) => new(
        BinaryPrimitives.ReadInt32LittleEndian(body[SumOffset..]),
        BinaryPrimitives.ReadInt32LittleEndian(body[CntOffset..]),
        BinaryPrimitives.ReadInt16LittleEndian(body[MaxOffset..]));

    public void Clear() => _ring.Clear();

    public void Dispose() => _ring.Dispose();
}
