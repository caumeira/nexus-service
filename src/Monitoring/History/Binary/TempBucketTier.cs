namespace Nexus.Service.Monitoring.History.Binary;

/// <summary>
/// Shared second/bucket-index conversions for TempBucketStore's rollup ring -
/// the same role MinuteTier plays for the 1-minute scalar/gpu/fan rollups,
/// at MetricsHistory.TempBucketMinutes' wider width.
/// </summary>
internal static class TempBucketTier
{
    public const int SecondsPerBucket = MetricsHistory.TempBucketMinutes * 60;

    /// <summary>The bucket floor a raw timestamp falls in - matches
    /// SQLite's <c>bucket_ts</c> (<c>ts/width*width</c>) exactly.</summary>
    public static long FloorToBucketSec(long sec) => sec / SecondsPerBucket * SecondsPerBucket;

    /// <summary>A bucket floor (already a multiple of SecondsPerBucket) to
    /// the ring's own addressing unit.</summary>
    public static long ToIndex(long bucketFloorSec) => bucketFloorSec / SecondsPerBucket;

    /// <summary>A cutoff (an arbitrary second, not necessarily
    /// bucket-aligned) to the bucket-index floor a ring should apply so it
    /// drops a bucket under the same rule
    /// SqliteMetricsHistoryStore.Prune's <c>bucket_ts &lt; cutoff</c> does -
    /// this needs the ceiling of cutoff/SecondsPerBucket, not the floor
    /// <see cref="ToIndex"/> uses for an already bucket-aligned value.
    /// Reused by TempBucketStore.Query for the same ceiling conversion on
    /// its window's lower bound (the smallest bucket index whose start is
    /// &gt;= fromSec).</summary>
    public static long ToPruneFloorIndex(long cutoffSec) =>
        cutoffSec == RingFile.UnwrittenStamp ? RingFile.UnwrittenStamp : (cutoffSec + SecondsPerBucket - 1) / SecondsPerBucket;
}
