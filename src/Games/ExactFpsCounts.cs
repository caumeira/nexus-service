namespace Nexus.Service.Games;

/// <summary>
/// Exact per-fps-value counts (0..MaxFps), kept in memory for one open
/// session only and never persisted - the capped test needs exact p10/p90,
/// which the persisted 64-bucket log histogram (FpsHistogram, ~11% wide
/// buckets) is too coarse for: a 60 Hz v-sync session straddling bucket
/// edges 54/60 would otherwise read as uncapped.
/// </summary>
internal static class ExactFpsCounts
{
    public const int MaxFps = 1000;

    public static void Add(uint[] counts, int frames)
    {
        if (frames < 0 || frames > MaxFps)
        {
            return;
        }
        counts[frames]++;
    }

    /// <summary>Same walk-the-buckets algorithm as FpsHistogram.Percentile,
    /// over exact integer buckets instead of log-spaced ones. 0 when empty.</summary>
    public static int Percentile(uint[] counts, double p)
    {
        long total = 0;
        foreach (var c in counts)
        {
            total += c;
        }
        if (total <= 0)
        {
            return 0;
        }

        var target = Math.Max(1, (long)Math.Ceiling(Math.Clamp(p, 0, 100) / 100.0 * total));
        long cumulative = 0;
        for (var fps = 0; fps < counts.Length; fps++)
        {
            cumulative += counts[fps];
            if (cumulative >= target)
            {
                return fps;
            }
        }
        return counts.Length - 1;
    }
}
