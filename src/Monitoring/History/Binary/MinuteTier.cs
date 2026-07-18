namespace Nexus.Service.Monitoring.History.Binary;

/// <summary>
/// Shared second/minute conversions for every ring that stores one slot per
/// minute (the binary equivalent of SqliteMetricsHistoryStore's
/// metric_minutes/gpu_minutes/fan_minutes): such a ring addresses its
/// RingFile by minute count since epoch, not raw epoch seconds, so a
/// MetricsHistory.RetentionDays window costs 1/60th the slots a per-second
/// ring over the same window would.
/// </summary>
internal static class MinuteTier
{
    public const int SecondsPerMinute = 60;

    /// <summary>The minute floor a raw timestamp falls in - matches SQLite's
    /// <c>ts_min</c> (<c>ts/60*60</c>) exactly.</summary>
    public static long FloorToMinuteSec(long sec) => sec / SecondsPerMinute * SecondsPerMinute;

    /// <summary>A minute floor (already a multiple of 60) to the ring's own
    /// addressing unit.</summary>
    public static long ToIndex(long minuteFloorSec) => minuteFloorSec / SecondsPerMinute;

    /// <summary>A prune cutoff (an arbitrary second, not necessarily
    /// minute-aligned) to the minute-index floor a ring should apply so it
    /// drops a minute under the same rule SqliteMetricsHistoryStore.Prune
    /// does (<c>ts_min &lt; cutoffSec</c> deletes the whole minute) - this
    /// needs the ceiling of cutoffSec/60, not the floor <see cref="ToIndex"/>
    /// uses for an already minute-aligned value, or a minute whose floor sits
    /// just below a non-minute-aligned cutoff would wrongly survive.</summary>
    public static long ToPruneFloorIndex(long cutoffSec) =>
        cutoffSec == RingFile.UnwrittenStamp ? RingFile.UnwrittenStamp : (cutoffSec + SecondsPerMinute - 1) / SecondsPerMinute;
}
