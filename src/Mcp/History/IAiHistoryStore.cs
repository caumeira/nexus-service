using System;
using System.Collections.Generic;

namespace Nexus.Service.Mcp.History;

/// <summary>Retention and bucket-width constants shared by the store's rollup/prune
/// logic and the read tools' tier-selection rule, so both sides stay in sync.</summary>
public static class AiHistoryRetention
{
    public const int RawRetentionMinutes = 30;
    public const int OneMinuteTierRetentionMinutes = 24 * 60;
    public const int FiveMinuteTierRetentionMinutes = 7 * 24 * 60;

    public const long OneMinuteBucketMs = 60_000L;
    public const long FiveMinuteBucketMs = 5 * 60_000L;

    /// <summary>Raw tier for windows within its own retention, 1-minute tier up to
    /// its retention, 5-minute tier beyond that (capped at its own retention by the caller).</summary>
    public static AiHistoryTier PickTier(int minutes)
    {
        if (minutes <= RawRetentionMinutes)
        {
            return AiHistoryTier.Raw;
        }
        return minutes <= OneMinuteTierRetentionMinutes ? AiHistoryTier.OneMinute : AiHistoryTier.FiveMinute;
    }
}

public enum AiHistoryTier
{
    Raw,
    OneMinute,
    FiveMinute,
}

/// <summary>One curated sensor reading for one recorder tick. Kind is a coarse
/// category ("temperature", "load", "power", "fan", "pump", "coolant") used only
/// for store bookkeeping - the wire payloads key off SensorId.</summary>
public readonly record struct AiHistorySampleRow(
    string SensorId, string Name, string Kind, string Unit, double Value, long TsUtcMs);

/// <summary>One point on a queried series: aggregate tiers report the bucket average.</summary>
public readonly record struct AiHistoryPointRow(long TUtcMs, double Value);

/// <summary>Result of querying one sensor's series over a window.</summary>
public sealed record AiHistorySeriesResult(
    string SensorId, string Name, string Unit, AiHistoryTier Tier, IReadOnlyList<AiHistoryPointRow> Points);

/// <summary>Min/max/avg/latest for one sensor over a window.</summary>
public sealed record AiHistorySensorSummaryRow(
    string SensorId, string Name, string Unit, double Min, double Max, double Avg, double Latest, long LatestAtUtcMs, int Samples);

/// <summary>One row recorded into the events table: an audited MCP write call or a lifecycle event.</summary>
public readonly record struct AiHistoryEventRow(
    long TsUtcMs, string Kind, string Name, string ArgsJson, bool Success, string? ErrorText);

public sealed record AiHistoryEventQueryResult(IReadOnlyList<AiHistoryEventRow> Events, bool Truncated);

/// <summary>
/// Persistent store for the MCP history subsystem: tiered sensor sample series
/// (raw / 1-minute / 5-minute aggregates) and the audit event log. The
/// implementation is <see cref="Nexus.Service.Mcp.History.Binary.BinaryAiHistoryStore"/>;
/// when it can't be opened, DI falls back to <see cref="UnavailableAiHistoryStore"/>
/// so the rest of the service keeps running and the history tools report
/// unavailability instead of throwing.
/// </summary>
public interface IAiHistoryStore : IDisposable
{
    /// <summary>False only for the unavailable fallback. Every write and query
    /// method on that fallback is a safe no-op / empty result.</summary>
    bool IsAvailable { get; }

    /// <summary>Inserts one tick's worth of raw samples, rolls up any newly
    /// completed 1-minute/5-minute buckets, and prunes rows past each tier's
    /// retention window - all in one transaction keyed off <paramref name="nowUtc"/>.</summary>
    void RecordSamples(IReadOnlyList<AiHistorySampleRow> rows, DateTime nowUtc);

    /// <summary>Every sensor id ever recorded and not yet fully pruned, for the
    /// unknown-sensor error text and as a fallback index.</summary>
    IReadOnlyList<string> KnownSensorIds();

    /// <summary>Null when sensorId has never been recorded (caller should list
    /// <see cref="KnownSensorIds"/> instead). An empty Points list is a known
    /// sensor with no data in this particular window, not an error.</summary>
    AiHistorySeriesResult? QuerySensorHistory(string sensorId, long fromUtcMs, long toUtcMs, int maxPoints);

    /// <summary>One summary row per sensor that has data in [fromUtcMs, toUtcMs].</summary>
    IReadOnlyList<AiHistorySensorSummaryRow> Summarize(long fromUtcMs, long toUtcMs);

    void RecordEvent(AiHistoryEventRow row);

    /// <summary>Newest first, capped at limit + 1 internally to compute Truncated.</summary>
    AiHistoryEventQueryResult QueryEvents(long fromUtcMs, string? type, int limit);
}
