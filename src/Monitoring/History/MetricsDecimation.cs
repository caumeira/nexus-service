using System;
using System.Collections.Generic;

namespace Nexus.Service.Monitoring.History;

/// <summary>One decimated chart point. T is a slot start on the internal
/// (epoch-seconds) time base coming out of Decimate; the route rewrites T to
/// UTC milliseconds and rounds Avg/Max before putting it on the wire, the
/// same "compute raw, round at the response layer" split
/// DiagnosticsHealthRoutes.BuildTemperatureResponse uses.</summary>
public sealed record MetricPoint(long T, double Avg, double Max);

/// <summary>One raw (ts, value) reading feeding Decimate - value is null
/// when the source failed that tick, which Decimate treats as "no data",
/// not zero.</summary>
public readonly record struct MetricSamplePoint(long TsSec, double? Value);

/// <summary>
/// Pure decimation for one metrics series: no I/O, fully unit-testable. Two
/// jobs: pick a step width off the shared ladder for a requested window/
/// maxPoints, then merge raw points into step-aligned slots. A slot with no
/// samples is omitted entirely (never interpolated), so a gap in the source
/// data renders as a real gap on the chart.
/// </summary>
public static class MetricsDecimation
{
    /// <summary>Smallest step in MetricsHistory.StepLadderSeconds that keeps
    /// windowSeconds within maxPoints slots; the widest ladder step if even
    /// that isn't enough.</summary>
    public static int StepSecondsFor(long windowSeconds, int maxPoints)
    {
        var points = Math.Max(1, maxPoints);
        var minStep = (long)Math.Ceiling(windowSeconds / (double)points);

        var ladder = MetricsHistory.StepLadderSeconds;
        foreach (var step in ladder)
        {
            if (step >= minStep)
            {
                return step;
            }
        }
        return ladder[^1];
    }

    /// <summary>Merges points (already deduped by caller - one value per ts)
    /// within [fromSec, toSec] into step-aligned slots (slot start =
    /// ts/step*step). Avg is the mean, Max is the max, of every non-null
    /// value landing in that slot; a slot with zero values is omitted. Points
    /// must already be ordered by TsSec - this streams slots as it goes and
    /// does not re-sort.</summary>
    public static IReadOnlyList<MetricPoint> Decimate(
        IEnumerable<MetricSamplePoint> points, long fromSec, long toSec, int stepSeconds)
    {
        var step = Math.Max(1, stepSeconds);
        var result = new List<MetricPoint>();

        long? currentSlot = null;
        double sum = 0;
        double max = double.MinValue;
        var count = 0;

        void CloseSlot()
        {
            if (currentSlot is { } slot && count > 0)
            {
                result.Add(new MetricPoint(slot, sum / count, max));
            }
            sum = 0;
            max = double.MinValue;
            count = 0;
        }

        foreach (var p in points)
        {
            if (p.TsSec < fromSec || p.TsSec > toSec || p.Value is not { } value)
            {
                continue;
            }

            var slot = p.TsSec / step * step;
            if (currentSlot is { } prevSlot && slot != prevSlot)
            {
                CloseSlot();
            }
            currentSlot = slot;
            sum += value;
            max = count == 0 ? value : Math.Max(max, value);
            count++;
        }
        CloseSlot();

        return result;
    }
}
