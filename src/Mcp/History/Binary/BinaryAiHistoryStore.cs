using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nexus.Service.Monitoring.History.Binary;

namespace Nexus.Service.Mcp.History.Binary;

/// <summary>
/// Binary-file-backed IAiHistoryStore (Phase 7b of the metrics-store
/// design): per-sensor tiered sample history plus an audit event log,
/// generalizing the metrics store's ScalarMinuteRollupRing pattern
/// (RingFile keyed by bucket index) from a fixed set of scalar fields to an
/// arbitrary, dynamically registered set of sensors - the same "array of
/// rings, one per identity, grown via an EnsureRingsExist-style array
/// publish" shape GpuRingStore uses for GPUs, generalized to three tiers
/// (raw/1-minute/5-minute) instead of two.
///
/// Sensor identity and mutable state (name/kind/unit/last value/last seen)
/// live in SensorMetaStore, keyed by the sensor's stable GlobalId - the
/// array index into every tier's per-sensor ring array. There is no entity
/// capacity cap unlike GpuRingStore/FanRingStore/TempComponentRingStore:
/// those bound an otherwise-hardware-driven, in principle unbounded id
/// space, while AiHistoryRecorder's own sensor set is a small, fixed,
/// curated list (CPU/GPU/fan/pump/coolant readings) with no path to
/// register an unbounded number of distinct sensor ids. RecordEvent/
/// QueryEvents are served by AiEventLog, a pure append log with no key to
/// upsert on.
///
/// Rollup is watermark-based: a 1-minute (or 5-minute) bucket only becomes
/// visible once a LATER RecordSamples call's nowUtc has moved past that
/// bucket's own end, matching SqliteAiHistoryStore's own
/// RollupRawToOneMinute/RollupOneMinuteToFiveMinute exactly - never the
/// eager "rebuild the touched bucket every write" pattern
/// ScalarMinuteRollupRing/GpuRingStore use elsewhere in this store family,
/// which would surface a still-forming bucket on every "up to now" query
/// that SqliteAiHistoryStore never shows. RollupOneMinuteIfDue/
/// RollupFiveMinuteIfDue track a persisted watermark (in floors.dat) and,
/// when it advances, rebuild every newly-closed bucket for every registered
/// sensor - the same set-based rollup SQL performs in one statement across
/// every sensor at once, rather than only the sensor(s) in this call's rows.
///
/// Each rollup's RingFileScan.Enumerate pass is bounded below by the
/// PREVIOUS watermark (in the source ring's own key units, or long.MinValue
/// only on the very first-ever rollup) - never by a wall-clock retention
/// width. Bounding by retention instead would both miss a bucket whenever a
/// real gap (a sleeping laptop, a service restart) exceeds it, and - the
/// sharper failure - re-touch and truncate an already-closed bucket on
/// every ordinary raw-ring wraparound (the ring physically reuses a slot
/// every RawRetentionMinutes of continuous operation, gap or not).
/// Bounding by the watermark keeps an already-closed bucket's key
/// permanently below the scan's lower bound, so it is never rebuilt again
/// regardless of what its ring slot holds later; RingFileScan's own
/// capacity-exceeded full-scan fallback still bounds the scan cost when a
/// gap is wide, so nothing here iterates every bucket since the store's
/// first write. A gap between two samples for the same sensor that is an
/// EXACT multiple of RawRetentionMinutes still collides at the physical
/// ring-slot level before rollup ever runs (the second write overwrites the
/// first) - a real, narrow divergence from SQL's row-based storage, judged
/// acceptable given AiHistoryRecorder's jittery ~5-second tick cadence.
///
/// A single lock serializes every method - the same low-volume-store choice
/// PrivacyLog/BinaryScreenTimeStore make, appropriate here since
/// AiHistoryRecorder ticks every 5 seconds, not once a second.
/// </summary>
public sealed class BinaryAiHistoryStore : IAiHistoryStore
{
    private const int RawBodyLength = 8 + 8; // value:f64, exactTsMs:i64
    private const int BucketBodyLength = 8 + 8 + 8 + 4; // min:f64, max:f64, sum:f64, count:i32

    private const long OneMinuteBucketMs = AiHistoryRetention.OneMinuteBucketMs;
    private const long FiveMinuteBucketMs = AiHistoryRetention.FiveMinuteBucketMs;

    private const long RawCapacitySeconds = AiHistoryRetention.RawRetentionMinutes * 60L;
    private const long OneMinCapacityBuckets = AiHistoryRetention.OneMinuteTierRetentionMinutes;
    private const long FiveMinCapacityBuckets = AiHistoryRetention.FiveMinuteTierRetentionMinutes / 5;

    private readonly string _dir;
    private readonly object _lock = new();
    private readonly SensorMetaStore _meta;
    private readonly AiEventLog _events;

    private RingFile[] _rawRings = Array.Empty<RingFile>();
    private RingFile[] _oneMinRings = Array.Empty<RingFile>();
    private RingFile[] _fiveMinRings = Array.Empty<RingFile>();

    private long _rawFloorSec = RingFile.UnwrittenStamp;
    private long _oneMinFloorIndex = RingFile.UnwrittenStamp;
    private long _fiveMinFloorIndex = RingFile.UnwrittenStamp;
    private long _oneMinWatermarkMs = RingFile.UnwrittenStamp;
    private long _fiveMinWatermarkMs = RingFile.UnwrittenStamp;

    public BinaryAiHistoryStore(string dir)
    {
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "raw"));
        Directory.CreateDirectory(Path.Combine(dir, "1m"));
        Directory.CreateDirectory(Path.Combine(dir, "5m"));
        _dir = dir;
        _meta = new SensorMetaStore(Path.Combine(dir, "sensors.meta"));
        _events = new AiEventLog(Path.Combine(dir, "events.log"));
        LoadFloors();

        if (_meta.MaxGlobalId >= 0)
        {
            EnsureRingsExist(_meta.MaxGlobalId);
        }
    }

    public bool IsAvailable => true;

    public void RecordSamples(IReadOnlyList<AiHistorySampleRow> rows, DateTime nowUtc)
    {
        if (rows.Count == 0)
        {
            return;
        }
        var nowUtcMs = new DateTimeOffset(nowUtc).ToUnixTimeMilliseconds();

        lock (_lock)
        {
            Span<byte> rawBody = stackalloc byte[RawBodyLength];
            foreach (var row in rows)
            {
                var idx = _meta.RegisterOrUpdate(row.SensorId, row.Name, row.Kind, row.Unit, row.Value, row.TsUtcMs);
                EnsureRingsExist(idx);

                var secondKey = row.TsUtcMs / 1000L;
                BinaryPrimitives.WriteDoubleLittleEndian(rawBody[..8], row.Value);
                BinaryPrimitives.WriteInt64LittleEndian(rawBody.Slice(8, 8), row.TsUtcMs);
                _rawRings[idx].WriteSlot(secondKey, rawBody);
            }

            RollupOneMinuteIfDue(nowUtcMs);
            RollupFiveMinuteIfDue(nowUtcMs);
            RaisePruneFloors(nowUtcMs);

            // Ring bytes must be durable before floors.dat records the
            // watermark that certifies them closed - the watermark never
            // revisits a bucket once it has advanced past it, so a crash
            // between the two would leave floors.dat claiming a bucket is
            // finalized while its actual bytes never reached disk. Same
            // ordering rule as AppUsageStore's id map reaching disk before
            // the day segment that references it.
            foreach (var ring in _rawRings)
            {
                ring.Flush();
            }
            foreach (var ring in _oneMinRings)
            {
                ring.Flush();
            }
            foreach (var ring in _fiveMinRings)
            {
                ring.Flush();
            }

            _meta.Persist();
            PersistFloors();
        }
    }

    public IReadOnlyList<string> KnownSensorIds()
    {
        lock (_lock)
        {
            return _meta.KnownSensorIdsSorted();
        }
    }

    public AiHistorySeriesResult? QuerySensorHistory(string sensorId, long fromUtcMs, long toUtcMs, int maxPoints)
    {
        lock (_lock)
        {
            if (_meta.TryGetEntry(sensorId) is not { } entry)
            {
                return null;
            }

            var minutes = (int)Math.Max(1, (toUtcMs - fromUtcMs) / 60_000L);
            var tier = AiHistoryRetention.PickTier(minutes);
            var points = tier switch
            {
                AiHistoryTier.Raw => QueryRawPoints(entry.GlobalId, fromUtcMs, toUtcMs),
                AiHistoryTier.OneMinute => QueryBucketPoints(_oneMinRings[entry.GlobalId], fromUtcMs, toUtcMs, OneMinuteBucketMs),
                _ => QueryBucketPoints(_fiveMinRings[entry.GlobalId], fromUtcMs, toUtcMs, FiveMinuteBucketMs),
            };

            return new AiHistorySeriesResult(sensorId, entry.Name, entry.Unit, tier, Thin(points, maxPoints));
        }
    }

    public IReadOnlyList<AiHistorySensorSummaryRow> Summarize(long fromUtcMs, long toUtcMs)
    {
        lock (_lock)
        {
            var minutes = (int)Math.Max(1, (toUtcMs - fromUtcMs) / 60_000L);
            var tier = AiHistoryRetention.PickTier(minutes);

            var result = new List<AiHistorySensorSummaryRow>();
            foreach (var entry in _meta.AllEntries())
            {
                var (min, max, sum, count) = tier switch
                {
                    AiHistoryTier.Raw => AggregateRaw(entry.GlobalId, fromUtcMs, toUtcMs),
                    AiHistoryTier.OneMinute => AggregateBuckets(_oneMinRings[entry.GlobalId], fromUtcMs, toUtcMs, OneMinuteBucketMs),
                    _ => AggregateBuckets(_fiveMinRings[entry.GlobalId], fromUtcMs, toUtcMs, FiveMinuteBucketMs),
                };
                if (count == 0)
                {
                    continue;
                }
                result.Add(new AiHistorySensorSummaryRow(
                    entry.SensorId, entry.Name, entry.Unit, min, max, sum / count, entry.LastValue, entry.LastSeenUtcMs, count));
            }
            return result;
        }
    }

    public void RecordEvent(AiHistoryEventRow row)
    {
        lock (_lock)
        {
            _events.Append(row);
        }
    }

    public AiHistoryEventQueryResult QueryEvents(long fromUtcMs, string? type, int limit)
    {
        lock (_lock)
        {
            return _events.QueryEvents(fromUtcMs, type, limit);
        }
    }

    public void Dispose()
    {
        foreach (var ring in _rawRings)
        {
            ring.Dispose();
        }
        foreach (var ring in _oneMinRings)
        {
            ring.Dispose();
        }
        foreach (var ring in _fiveMinRings)
        {
            ring.Dispose();
        }
    }

    private void EnsureRingsExist(int idx)
    {
        while (_rawRings.Length <= idx)
        {
            var i = _rawRings.Length;
            var nextRaw = new RingFile[i + 1];
            Array.Copy(_rawRings, nextRaw, i);
            nextRaw[i] = RingFile.CreateOrOpen(Path.Combine(_dir, "raw", $"{i}.ring"), RawCapacitySeconds, RawBodyLength, _rawFloorSec);

            var nextOneMin = new RingFile[i + 1];
            Array.Copy(_oneMinRings, nextOneMin, i);
            nextOneMin[i] = RingFile.CreateOrOpen(Path.Combine(_dir, "1m", $"{i}.ring"), OneMinCapacityBuckets, BucketBodyLength, _oneMinFloorIndex);

            var nextFiveMin = new RingFile[i + 1];
            Array.Copy(_fiveMinRings, nextFiveMin, i);
            nextFiveMin[i] = RingFile.CreateOrOpen(Path.Combine(_dir, "5m", $"{i}.ring"), FiveMinCapacityBuckets, BucketBodyLength, _fiveMinFloorIndex);

            _rawRings = nextRaw;
            _oneMinRings = nextOneMin;
            _fiveMinRings = nextFiveMin;
        }
    }

    // Rolls up every 1-minute bucket that has newly closed (its end is at or
    // before nowUtcMs's own minute floor) since the last call, for every
    // registered sensor - matching SqliteAiHistoryStore.RollupRawToOneMinute
    // running once across every sensor's raw rows in the same closing
    // range, rather than only the sensor(s) present in this call's rows.
    // The scan's lower bound is the PREVIOUS watermark (or unbounded on the
    // very first-ever rollup, when nothing has been closed yet), not
    // nowUtcMs minus a fixed retention width: SqliteAiHistoryStore's own
    // RollupRawToOneMinute is bounded the same way (its own lastRollup1m
    // watermark, never a wall-clock cap), so this tolerates an arbitrarily
    // wide gap between calls without missing a bucket. Bounding below by
    // nowUtcMs minus a fixed retention instead - tried first and wrong -
    // would rescan and rebuild already-closed buckets on every ordinary
    // ring wraparound (once every RawRetentionMinutes of continuous
    // operation, not only after a gap): the raw ring physically reuses a
    // slot every wraparound, so a bucket already finalized earlier can read
    // back with a truncated, wrong aggregate if this rebuild ever revisits
    // it - the watermark bound is what keeps every already-closed bucket
    // untouched forever after.
    private void RollupOneMinuteIfDue(long nowUtcMs)
    {
        var bucketFloorMs = FloorToMs(nowUtcMs, OneMinuteBucketMs);
        if (bucketFloorMs <= _oneMinWatermarkMs)
        {
            return;
        }

        var fromSec = _oneMinWatermarkMs == RingFile.UnwrittenStamp ? long.MinValue : _oneMinWatermarkMs / 1000L;
        var toSec = bucketFloorMs / 1000L - 1;
        for (var idx = 0; idx < _rawRings.Length; idx++)
        {
            var minutesWithData = new HashSet<long>();
            foreach (var (secondKey, _) in RingFileScan.Enumerate(_rawRings[idx], fromSec, toSec))
            {
                minutesWithData.Add(secondKey / 60L);
            }
            foreach (var minuteIndex in minutesWithData)
            {
                RebuildOneMinuteBucket(idx, minuteIndex);
            }
        }
        _oneMinWatermarkMs = bucketFloorMs;
    }

    // Same shape as RollupOneMinuteIfDue, one tier up: rolls up every
    // 5-minute bucket that has newly closed from its five constituent
    // 1-minute buckets (the source ring for this rollup), bounded below by
    // the previous 5-minute watermark for the same reason.
    private void RollupFiveMinuteIfDue(long nowUtcMs)
    {
        var bucketFloorMs = FloorToMs(nowUtcMs, FiveMinuteBucketMs);
        if (bucketFloorMs <= _fiveMinWatermarkMs)
        {
            return;
        }

        var fromMinuteIndex = _fiveMinWatermarkMs == RingFile.UnwrittenStamp ? long.MinValue : _fiveMinWatermarkMs / OneMinuteBucketMs;
        var toMinuteIndex = bucketFloorMs / OneMinuteBucketMs - 1;
        for (var idx = 0; idx < _oneMinRings.Length; idx++)
        {
            var bucketsWithData = new HashSet<long>();
            foreach (var (minuteIndex, _) in RingFileScan.Enumerate(_oneMinRings[idx], fromMinuteIndex, toMinuteIndex))
            {
                bucketsWithData.Add(minuteIndex / 5L);
            }
            foreach (var fiveMinIndex in bucketsWithData)
            {
                RebuildFiveMinuteBucket(idx, fiveMinIndex);
            }
        }
        _fiveMinWatermarkMs = bucketFloorMs;
    }

    private static long FloorToMs(long ms, long bucketWidthMs) => ms / bucketWidthMs * bucketWidthMs;

    // Callers only invoke this for a minute the enumerate pass already found
    // data in, so this guard is a direct check rather than an assumption -
    // a wrapped ring slot that read back stale would otherwise write a
    // bucket claiming data that isn't there.
    private void RebuildOneMinuteBucket(int idx, long minuteIndex)
    {
        var fromSec = minuteIndex * 60L;
        var toSec = fromSec + 59;
        double? min = null;
        double? max = null;
        double sum = 0;
        var count = 0;
        foreach (var (_, body) in RingFileScan.Enumerate(_rawRings[idx], fromSec, toSec))
        {
            var value = BinaryPrimitives.ReadDoubleLittleEndian(body.AsSpan(0, 8));
            min = min is null ? value : Math.Min(min.Value, value);
            max = max is null ? value : Math.Max(max.Value, value);
            sum += value;
            count++;
        }
        if (count == 0)
        {
            return;
        }
        WriteBucket(_oneMinRings[idx], minuteIndex, min!.Value, max!.Value, sum, count);
    }

    private void RebuildFiveMinuteBucket(int idx, long fiveMinIndex)
    {
        var fromMinuteIndex = fiveMinIndex * 5;
        var toMinuteIndex = fromMinuteIndex + 4;
        double? min = null;
        double? max = null;
        double sum = 0;
        var count = 0;
        foreach (var (_, body) in RingFileScan.Enumerate(_oneMinRings[idx], fromMinuteIndex, toMinuteIndex))
        {
            var (bMin, bMax, bSum, bCount) = DecodeBucket(body);
            if (bCount == 0)
            {
                continue;
            }
            min = min is null ? bMin : Math.Min(min.Value, bMin);
            max = max is null ? bMax : Math.Max(max.Value, bMax);
            sum += bSum;
            count += bCount;
        }
        if (count == 0)
        {
            return;
        }
        WriteBucket(_fiveMinRings[idx], fiveMinIndex, min!.Value, max!.Value, sum, count);
    }

    private static void WriteBucket(RingFile ring, long index, double min, double max, double sum, int count)
    {
        Span<byte> body = stackalloc byte[BucketBodyLength];
        BinaryPrimitives.WriteDoubleLittleEndian(body[..8], min);
        BinaryPrimitives.WriteDoubleLittleEndian(body.Slice(8, 8), max);
        BinaryPrimitives.WriteDoubleLittleEndian(body.Slice(16, 8), sum);
        BinaryPrimitives.WriteInt32LittleEndian(body.Slice(24, 4), count);
        ring.WriteSlot(index, body);
    }

    private static (double Min, double Max, double Sum, int Count) DecodeBucket(ReadOnlySpan<byte> body) => (
        BinaryPrimitives.ReadDoubleLittleEndian(body[..8]),
        BinaryPrimitives.ReadDoubleLittleEndian(body.Slice(8, 8)),
        BinaryPrimitives.ReadDoubleLittleEndian(body.Slice(16, 8)),
        BinaryPrimitives.ReadInt32LittleEndian(body.Slice(24, 4)));

    private List<AiHistoryPointRow> QueryRawPoints(int idx, long fromUtcMs, long toUtcMs)
    {
        var fromSec = fromUtcMs / 1000L;
        var toSec = toUtcMs / 1000L;
        var result = new List<AiHistoryPointRow>();
        foreach (var (_, body) in RingFileScan.Enumerate(_rawRings[idx], fromSec, toSec))
        {
            var value = BinaryPrimitives.ReadDoubleLittleEndian(body.AsSpan(0, 8));
            var exactTsMs = BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(8, 8));
            if (exactTsMs >= fromUtcMs && exactTsMs <= toUtcMs)
            {
                result.Add(new AiHistoryPointRow(exactTsMs, value));
            }
        }
        result.Sort((a, b) => a.TUtcMs.CompareTo(b.TUtcMs));
        return result;
    }

    // fromIndex uses ceiling, not floor: matching SqliteAiHistoryStore's
    // exact "bucket_utc >= from" filter, a bucket whose own start (its key)
    // sits strictly before fromUtcMs is excluded even when fromUtcMs falls
    // inside that bucket's span - a bucket is a point (its start), not an
    // interval, for this comparison. toIndex stays floor: the largest
    // bucket start at or before toUtcMs is exactly floor(toUtcMs/width).
    private static List<AiHistoryPointRow> QueryBucketPoints(RingFile ring, long fromUtcMs, long toUtcMs, long bucketWidthMs)
    {
        var fromIndex = CeilDiv(fromUtcMs, bucketWidthMs);
        var toIndex = toUtcMs / bucketWidthMs;
        var result = new List<AiHistoryPointRow>();
        foreach (var (key, body) in RingFileScan.Enumerate(ring, fromIndex, toIndex))
        {
            var (_, _, sum, count) = DecodeBucket(body);
            if (count > 0)
            {
                result.Add(new AiHistoryPointRow(key * bucketWidthMs, sum / count));
            }
        }
        return result;
    }

    private (double Min, double Max, double Sum, int Count) AggregateRaw(int idx, long fromUtcMs, long toUtcMs)
    {
        var points = QueryRawPoints(idx, fromUtcMs, toUtcMs);
        if (points.Count == 0)
        {
            return (0, 0, 0, 0);
        }
        return (points.Min(p => p.Value), points.Max(p => p.Value), points.Sum(p => p.Value), points.Count);
    }

    private static (double Min, double Max, double Sum, int Count) AggregateBuckets(RingFile ring, long fromUtcMs, long toUtcMs, long bucketWidthMs)
    {
        var fromIndex = CeilDiv(fromUtcMs, bucketWidthMs);
        var toIndex = toUtcMs / bucketWidthMs;
        double? min = null;
        double? max = null;
        double sum = 0;
        var count = 0;
        foreach (var (_, body) in RingFileScan.Enumerate(ring, fromIndex, toIndex))
        {
            var (bMin, bMax, bSum, bCount) = DecodeBucket(body);
            if (bCount == 0)
            {
                continue;
            }
            min = min is null ? bMin : Math.Min(min.Value, bMin);
            max = max is null ? bMax : Math.Max(max.Value, bMax);
            sum += bSum;
            count += bCount;
        }
        return (min ?? 0, max ?? 0, sum, count);
    }

    // Byte-for-byte the same stride-thinning algorithm as
    // SqliteAiHistoryStore.Thin: a pure function over an already-materialized
    // point list, no store-specific behavior to diverge on.
    private static IReadOnlyList<AiHistoryPointRow> Thin(List<AiHistoryPointRow> points, int maxPoints)
    {
        if (maxPoints <= 0 || points.Count <= maxPoints)
        {
            return points;
        }
        var stride = (int)Math.Ceiling(points.Count / (double)maxPoints);
        var result = new List<AiHistoryPointRow>();
        for (var i = 0; i < points.Count; i += stride)
        {
            result.Add(points[i]);
        }
        if ((result.Count - 1) * stride != points.Count - 1)
        {
            result.Add(points[^1]);
        }
        return result;
    }

    private void RaisePruneFloors(long nowUtcMs)
    {
        var newRawFloor = CeilDiv(nowUtcMs - AiHistoryRetention.RawRetentionMinutes * 60_000L, 1000L);
        var newOneMinFloor = CeilDiv(nowUtcMs - AiHistoryRetention.OneMinuteTierRetentionMinutes * 60_000L, 60_000L);
        var newFiveMinFloor = CeilDiv(nowUtcMs - AiHistoryRetention.FiveMinuteTierRetentionMinutes * 60_000L, 300_000L);

        if (newRawFloor > _rawFloorSec)
        {
            _rawFloorSec = newRawFloor;
            foreach (var ring in _rawRings)
            {
                ring.PruneFloorSec = newRawFloor;
            }
        }
        if (newOneMinFloor > _oneMinFloorIndex)
        {
            _oneMinFloorIndex = newOneMinFloor;
            foreach (var ring in _oneMinRings)
            {
                ring.PruneFloorSec = newOneMinFloor;
            }
        }
        if (newFiveMinFloor > _fiveMinFloorIndex)
        {
            _fiveMinFloorIndex = newFiveMinFloor;
            foreach (var ring in _fiveMinRings)
            {
                ring.PruneFloorSec = newFiveMinFloor;
            }
        }
    }

    private static long CeilDiv(long value, long divisor) => (value + divisor - 1) / divisor;

    private void LoadFloors()
    {
        var path = Path.Combine(_dir, "floors.dat");
        if (!File.Exists(path))
        {
            return;
        }
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length != 40)
        {
            return;
        }
        _rawFloorSec = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(0, 8));
        _oneMinFloorIndex = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(8, 8));
        _fiveMinFloorIndex = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(16, 8));
        _oneMinWatermarkMs = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(24, 8));
        _fiveMinWatermarkMs = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(32, 8));
    }

    private void PersistFloors()
    {
        var path = Path.Combine(_dir, "floors.dat");
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            Span<byte> buf = stackalloc byte[40];
            BinaryPrimitives.WriteInt64LittleEndian(buf[..8], _rawFloorSec);
            BinaryPrimitives.WriteInt64LittleEndian(buf.Slice(8, 8), _oneMinFloorIndex);
            BinaryPrimitives.WriteInt64LittleEndian(buf.Slice(16, 8), _fiveMinFloorIndex);
            BinaryPrimitives.WriteInt64LittleEndian(buf.Slice(24, 8), _oneMinWatermarkMs);
            BinaryPrimitives.WriteInt64LittleEndian(buf.Slice(32, 8), _fiveMinWatermarkMs);
            fs.Write(buf);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }
}
