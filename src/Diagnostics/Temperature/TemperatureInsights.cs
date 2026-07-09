using System;
using System.Collections.Generic;
using System.Linq;

namespace Nexus.Service.Diagnostics.Temperature;

/// <summary>One chart point: t is the bucket's start (UTC ms).</summary>
public sealed record TemperaturePoint(long T, double Avg, double Max);

/// <summary>A sustained-high-temperature window for one component.</summary>
public sealed record TemperatureEpisode(
    string ComponentId, string Name, DateTime StartUtc, DateTime EndUtc,
    double PeakC, double ThresholdC);

/// <summary>
/// Pure analysis over stored temperature buckets: no I/O, fully unit-testable.
/// Sustained-high detection and chart decimation both operate on
/// TemperatureBucketRow lists already read from the store.
/// </summary>
public static class TemperatureInsights
{
    private const double CpuThresholdC = 90;
    private const double GpuThresholdC = 85;
    private const double StorageThresholdC = 70;
    private const double RamThresholdC = 60;

    // A single above-threshold bucket can be a noisy sample, not sustained
    // heat; requiring more than one in a row filters that out.
    private const int MinConsecutiveBuckets = 2;

    public static double? ThresholdFor(string kind) => kind switch
    {
        "cpu" => CpuThresholdC,
        "gpu" => GpuThresholdC,
        "storage" => StorageThresholdC,
        "ram" => RamThresholdC,
        _ => null,
    };

    /// <summary>Chart granularity tier for a requested window: wider windows get a
    /// coarser target bucket width in minutes, bounding merged point counts by
    /// width instead of relying on a raw point cap alone.</summary>
    public static int TierWidthMinutesFor(int windowHours) => windowHours switch
    {
        <= 24 => 5,
        <= 72 => 15,
        <= 168 => 30,
        _ => 60,
    };

    /// <summary>
    /// Finds every run of at least <see cref="MinConsecutiveBuckets"/> time-adjacent
    /// buckets whose AvgC is at or above the kind's threshold, per component. A
    /// missing bucket (a gap in bucket_utc) breaks a run even if both sides are
    /// above threshold - the run must be one unbroken stretch of sampled buckets.
    /// </summary>
    public static IReadOnlyList<TemperatureEpisode> DetectEpisodes(IReadOnlyList<TemperatureBucketRow> rows)
    {
        var bucketMs = TemperatureSampler.BucketMinutes * 60_000L;
        var episodes = new List<TemperatureEpisode>();

        foreach (var group in rows.GroupBy(r => r.ComponentId))
        {
            var ordered = group.OrderBy(r => r.BucketUtcMs).ToList();
            if (ThresholdFor(ordered[0].Kind) is not { } thresholdC)
            {
                continue;
            }

            long? streakStartMs = null;
            long? streakLastMs = null;
            var streakLen = 0;
            var streakPeak = 0.0;
            var streakName = ordered[0].Name;

            void CloseStreak()
            {
                if (streakLen >= MinConsecutiveBuckets)
                {
                    episodes.Add(new TemperatureEpisode(
                        group.Key,
                        streakName,
                        DateTimeOffset.FromUnixTimeMilliseconds(streakStartMs!.Value).UtcDateTime,
                        DateTimeOffset.FromUnixTimeMilliseconds(streakLastMs!.Value + bucketMs).UtcDateTime,
                        streakPeak,
                        thresholdC));
                }
                streakLen = 0;
                streakStartMs = null;
                streakPeak = 0.0;
            }

            foreach (var row in ordered)
            {
                var above = row.AvgC >= thresholdC;
                var contiguous = streakLastMs is { } lastMs && row.BucketUtcMs - lastMs == bucketMs;

                if (above && streakLen > 0 && contiguous)
                {
                    streakLen++;
                    streakPeak = Math.Max(streakPeak, row.MaxC);
                    streakName = row.Name;
                }
                else if (above)
                {
                    CloseStreak();
                    streakLen = 1;
                    streakStartMs = row.BucketUtcMs;
                    streakPeak = row.MaxC;
                    streakName = row.Name;
                }
                else
                {
                    CloseStreak();
                }
                streakLastMs = row.BucketUtcMs;
            }
            CloseStreak();
        }

        return episodes.OrderBy(e => e.StartUtc).ToList();
    }

    /// <summary>
    /// Merges buckets into fixed-width slots aligned to widthMs boundaries (slot
    /// start = bucketUtc / widthMs * widthMs), so merged points land on the same
    /// grid positions across requests instead of drifting with row count. avg is
    /// the samples-weighted average, max is the max of maxes; ComponentId/Kind/Name
    /// come from the slot's last row. Rows must already be ordered by BucketUtcMs.
    /// A no-op when widthMs is not wider than the raw bucket width.
    /// </summary>
    public static IReadOnlyList<TemperatureBucketRow> MergeToWidth(IReadOnlyList<TemperatureBucketRow> rows, long widthMs)
    {
        const long rawBucketMs = TemperatureSampler.BucketMinutes * 60_000L;
        if (rows.Count == 0 || widthMs <= rawBucketMs)
        {
            return rows;
        }

        var result = new List<TemperatureBucketRow>();
        var chunk = new List<TemperatureBucketRow>();
        var currentSlot = 0L;

        foreach (var row in rows)
        {
            var slot = row.BucketUtcMs / widthMs * widthMs;
            if (chunk.Count > 0 && slot != currentSlot)
            {
                result.Add(MergeChunk(chunk, currentSlot));
                chunk.Clear();
            }
            currentSlot = slot;
            chunk.Add(row);
        }
        if (chunk.Count > 0)
        {
            result.Add(MergeChunk(chunk, currentSlot));
        }
        return result;
    }

    private static TemperatureBucketRow MergeChunk(List<TemperatureBucketRow> chunk, long slotStartMs)
    {
        var totalSamples = chunk.Sum(r => r.Samples);
        var avg = totalSamples > 0
            ? chunk.Sum(r => r.AvgC * r.Samples) / totalSamples
            : chunk.Average(r => r.AvgC);
        var max = chunk.Max(r => r.MaxC);
        return chunk[^1] with { BucketUtcMs = slotStartMs, AvgC = avg, MaxC = max, Samples = totalSamples };
    }

    /// <summary>
    /// Merges adjacent buckets so a series never exceeds maxPoints: avg is the
    /// samples-weighted average of the merged buckets' averages, max is the
    /// max of their maxes, t is the first merged bucket's start.
    /// </summary>
    public static IReadOnlyList<TemperaturePoint> Decimate(IReadOnlyList<TemperatureBucketRow> rows, int maxPoints)
    {
        if (rows.Count == 0)
        {
            return Array.Empty<TemperaturePoint>();
        }
        if (rows.Count <= maxPoints)
        {
            return rows.Select(r => new TemperaturePoint(r.BucketUtcMs, r.AvgC, r.MaxC)).ToList();
        }

        var groupSize = (int)Math.Ceiling(rows.Count / (double)maxPoints);
        var result = new List<TemperaturePoint>();
        for (var i = 0; i < rows.Count; i += groupSize)
        {
            var chunk = rows.Skip(i).Take(groupSize).ToList();
            var totalSamples = chunk.Sum(r => r.Samples);
            var avg = totalSamples > 0
                ? chunk.Sum(r => r.AvgC * r.Samples) / totalSamples
                : chunk.Average(r => r.AvgC);
            var max = chunk.Max(r => r.MaxC);
            result.Add(new TemperaturePoint(chunk[0].BucketUtcMs, avg, max));
        }
        return result;
    }
}
