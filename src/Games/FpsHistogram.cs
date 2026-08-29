namespace Nexus.Service.Games;

/// <summary>
/// Shared fps histogram bucket math: 64 log-spaced buckets over 1..1000 fps
/// (edge(i) = 10^(3i/64), about 11% wide), exactly mergeable bucket-wise so
/// every percentile the cloud reports later is derived rather than stored.
/// Used by FpsSessionRecorder (per-second accumulation, capped detection)
/// and BinaryFpsSessionStore's per-game aggregation (histogram merge,
/// percentile derivation).
/// </summary>
public static class FpsHistogram
{
    public const int BucketCount = 64;

    /// <summary>Lower edge of bucket i for i in 0..BucketCount inclusive;
    /// EdgeFps(BucketCount) is the histogram's upper bound, 1000.</summary>
    public static double EdgeFps(int i) => Math.Pow(10, 3.0 * i / BucketCount);

    /// <summary>Bucket index for an fps reading, clamped to the 1..1000
    /// histogram range - callers apply the 1&lt;=frames&lt;=1000 validity
    /// rule before deciding whether to call this at all.</summary>
    public static int BucketIndex(int fps)
    {
        var clamped = Math.Clamp(fps, 1, 1000);
        var index = (int)Math.Floor(BucketCount * Math.Log10(clamped) / 3.0);
        return Math.Clamp(index, 0, BucketCount - 1);
    }

    public static void AddSample(uint[] hist, int fps) => hist[BucketIndex(fps)]++;

    public static long Total(IReadOnlyList<uint> hist)
    {
        long total = 0;
        foreach (var c in hist)
        {
            total += c;
        }
        return total;
    }

    /// <summary>Bucket-wise sum of two equal-length histograms.</summary>
    public static uint[] Merge(IReadOnlyList<uint> a, IReadOnlyList<uint> b)
    {
        var result = new uint[BucketCount];
        for (var i = 0; i < BucketCount; i++)
        {
            result[i] = a[i] + b[i];
        }
        return result;
    }

    /// <summary>Approximate percentile fps value: walks buckets low to high
    /// accumulating counts until the target rank is reached, returning that
    /// bucket's lower edge rounded to a whole fps. 0 when the histogram is
    /// empty. p is a percentile in 0..100.</summary>
    public static int Percentile(IReadOnlyList<uint> hist, double p)
    {
        var total = Total(hist);
        if (total <= 0)
        {
            return 0;
        }

        var target = Math.Max(1, (long)Math.Ceiling(Math.Clamp(p, 0, 100) / 100.0 * total));

        long cumulative = 0;
        for (var i = 0; i < hist.Count; i++)
        {
            cumulative += hist[i];
            if (cumulative >= target)
            {
                return (int)Math.Round(EdgeFps(i));
            }
        }
        return (int)Math.Round(EdgeFps(hist.Count - 1));
    }
}
