using System;
using System.IO;
using Nexus.Service.Mcp.History;
using Nexus.Service.Mcp.History.Binary;
using Xunit;

namespace Nexus.Service.Tests.Mcp;

/// <summary>
/// The AI/MCP history behavior IAiHistoryStore's methods must have: raw
/// sample round-trip, tier selection by window width, minute/5-minute
/// rollup, per-tier retention pruning, sensor summary math (including
/// Latest/LatestAtUtcMs coming from the sensor's own last-recorded state,
/// not the queried window), point thinning, and the event audit log. Runs
/// against BinaryAiHistoryStore (BinaryAiHistoryStoreSpecTests). Timestamps
/// are synthetic (a fixed epoch stepped by hand) so the tiering/rollup math
/// is deterministic and does not depend on wall-clock time.
/// </summary>
public abstract class AiHistoryStoreSpec : IDisposable
{
    private static readonly DateTime BaseUtc = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _dir;
    protected IAiHistoryStore Store = null!;

    protected AiHistoryStoreSpec()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-aihistory-spec-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        Store = CreateStore(_dir);
    }

    protected abstract IAiHistoryStore CreateStore(string dir);

    /// <summary>Disposes the current store and reopens a fresh instance at
    /// the same directory, simulating a service restart.</summary>
    protected void Reopen()
    {
        Store.Dispose();
        Store = CreateStore(_dir);
    }

    public virtual void Dispose()
    {
        Store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static long ToMs(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();

    private static AiHistorySampleRow Row(string sensorId, double value, DateTime t,
        string name = "Sensor", string kind = "temperature", string unit = "C") =>
        new(sensorId, name, kind, unit, value, ToMs(t));

    [Fact]
    public void RecordSamples_then_QuerySensorHistory_round_trips_a_raw_point()
    {
        Store.RecordSamples(new[] { Row("cpu-temp", 55.5, BaseUtc, name: "CPU Temperature") }, BaseUtc);

        var series = Store.QuerySensorHistory("cpu-temp", ToMs(BaseUtc) - 1000, ToMs(BaseUtc) + 1000, maxPoints: 10);

        Assert.NotNull(series);
        Assert.Equal(AiHistoryTier.Raw, series!.Tier);
        Assert.Equal("CPU Temperature", series.Name);
        Assert.Equal("C", series.Unit);
        var point = Assert.Single(series.Points);
        Assert.Equal(55.5, point.Value);
    }

    [Fact]
    public void QuerySensorHistory_unknown_sensor_returns_null()
    {
        Assert.Null(Store.QuerySensorHistory("does-not-exist", 0, ToMs(BaseUtc), maxPoints: 10));
    }

    [Fact]
    public void KnownSensorIds_lists_every_sensor_ever_recorded()
    {
        Store.RecordSamples(new[] { Row("cpu-temp", 40, BaseUtc), Row("gpu-temp", 50, BaseUtc) }, BaseUtc);

        var ids = Store.KnownSensorIds();

        Assert.Equal(new[] { "cpu-temp", "gpu-temp" }, ids);
    }

    [Fact]
    public void A_completed_minute_bucket_rolls_up_into_the_1m_tier_as_an_average()
    {
        for (var i = 0; i < 12; i++)
        {
            var t = BaseUtc.AddSeconds(i * 5); // 0, 5, ..., 55 - all inside minute 0
            Store.RecordSamples(new[] { Row("cpu-temp", 40 + i, t) }, t);
        }
        var trigger = BaseUtc.AddSeconds(65);
        Store.RecordSamples(new[] { Row("cpu-temp", 99, trigger) }, trigger);

        var series = Store.QuerySensorHistory("cpu-temp", ToMs(BaseUtc), ToMs(BaseUtc) + 40 * 60_000L, maxPoints: 100);

        Assert.NotNull(series);
        Assert.Equal(AiHistoryTier.OneMinute, series!.Tier);
        // Exactly one point: minute 1 (holding the trigger sample) has not
        // closed yet and must not appear alongside minute 0.
        var bucket = Assert.Single(series.Points);
        Assert.Equal(ToMs(BaseUtc), bucket.TUtcMs);
        Assert.Equal(45.5, bucket.Value); // average of 40..51
    }

    [Fact]
    public void A_completed_five_minute_span_of_1m_buckets_rolls_up_into_the_5m_tier()
    {
        for (var i = 0; i < 6; i++)
        {
            var t = BaseUtc.AddMinutes(i).AddSeconds(1);
            Store.RecordSamples(new[] { Row("cpu-temp", 10 + i, t) }, t);
        }

        var series = Store.QuerySensorHistory("cpu-temp", ToMs(BaseUtc), ToMs(BaseUtc) + 2000 * 60_000L, maxPoints: 100);

        Assert.NotNull(series);
        Assert.Equal(AiHistoryTier.FiveMinute, series!.Tier);
        // Exactly one point: the second 5-minute bucket (minute 5 landed in
        // it) has not closed yet and must not appear alongside the first.
        var bucket = Assert.Single(series.Points);
        Assert.Equal(ToMs(BaseUtc), bucket.TUtcMs);
        Assert.Equal(12.0, bucket.Value, precision: 6); // average of 10,11,12,13,14
    }

    [Fact]
    public void A_sample_recorded_before_a_long_gap_still_rolls_up_once_a_later_call_closes_its_minute()
    {
        // No RecordSamples call happens for the whole gap (a sleeping
        // laptop, a service restart) - the rollup must still find the first
        // sample once a later call finally closes its minute, not lose it
        // because the gap was wider than any fixed lookback window. 45
        // minutes is comfortably past the raw tier's own 30-minute
        // retention, so a rollup that (wrongly) capped its lookback at that
        // retention would miss this sample entirely.
        Store.RecordSamples(new[] { Row("s1", 40, BaseUtc) }, BaseUtc);

        var afterGap = BaseUtc.AddMinutes(45);
        Store.RecordSamples(new[] { Row("s1", 41, afterGap) }, afterGap);

        var series = Store.QuerySensorHistory("s1", ToMs(BaseUtc) - 1000, ToMs(BaseUtc.AddMinutes(44)), maxPoints: 100);

        Assert.NotNull(series);
        Assert.Equal(AiHistoryTier.OneMinute, series!.Tier);
        var point = Assert.Single(series.Points);
        Assert.Equal(ToMs(BaseUtc), point.TUtcMs);
        Assert.Equal(40, point.Value);
    }

    [Fact]
    public void Rollup_DoesNotCorruptAnAlreadyClosedBucket_WhenTickingContinuouslyThroughARawRingWraparound()
    {
        // Minute 0 gets exactly the samples a 5-second tick cadence lands
        // in it (seconds 0..55, 12 samples) and closes once tick i=12
        // reaches minute 1. Ticking on with no gap at all past
        // RawRetentionMinutes then cycles the raw ring's physical capacity
        // all the way around - minute 0's own aggregate must stay exactly
        // what it was the moment it closed, not get rebuilt from whatever
        // partial data the ring's later reuse of those same slots leaves
        // behind.
        var lastTickSecond = AiHistoryRetention.RawRetentionMinutes * 60 + 60;
        for (var second = 0; second <= lastTickSecond; second += 5)
        {
            var t = BaseUtc.AddSeconds(second);
            Store.RecordSamples(new[] { Row("s1", second / 5, t) }, t);
        }

        var series = Store.QuerySensorHistory("s1", ToMs(BaseUtc), ToMs(BaseUtc) + 40 * 60_000L, maxPoints: 1000);

        Assert.NotNull(series);
        Assert.Equal(AiHistoryTier.OneMinute, series!.Tier);
        var minuteZero = Assert.Single(series.Points, p => p.TUtcMs == ToMs(BaseUtc));
        Assert.Equal(5.5, minuteZero.Value); // average of ticks 0..11 (values 0..11), unchanged by the later wraparound
    }

    [Fact]
    public void QuerySensorHistory_OneMinuteTier_ExcludesABucket_WhoseStartIsBeforeANonAlignedWindowStart()
    {
        Store.RecordSamples(new[] { Row("s1", 10, BaseUtc) }, BaseUtc);
        Store.RecordSamples(new[] { Row("s1", 20, BaseUtc.AddMinutes(1)) }, BaseUtc.AddMinutes(1)); // closes minute 0
        Store.RecordSamples(new[] { Row("s1", 30, BaseUtc.AddMinutes(2)) }, BaseUtc.AddMinutes(2)); // closes minute 1

        // fromUtcMs lands one second after minute 0's own start - a bucket
        // is keyed by its start, so minute 0 must be excluded even though
        // its span technically still reaches into the window.
        var series = Store.QuerySensorHistory(
            "s1", ToMs(BaseUtc.AddSeconds(1)), ToMs(BaseUtc.AddMinutes(35)), maxPoints: 100);

        Assert.NotNull(series);
        Assert.Equal(AiHistoryTier.OneMinute, series!.Tier);
        var point = Assert.Single(series.Points);
        Assert.Equal(ToMs(BaseUtc.AddMinutes(1)), point.TUtcMs);
        Assert.Equal(20, point.Value);
    }

    [Fact]
    public void Raw_rows_are_pruned_once_they_pass_the_raw_retention_window()
    {
        Store.RecordSamples(new[] { Row("s1", 40, BaseUtc) }, BaseUtc);

        var later = BaseUtc.AddMinutes(AiHistoryRetention.RawRetentionMinutes + 1);
        Store.RecordSamples(new[] { Row("s1", 41, later) }, later);

        var series = Store.QuerySensorHistory("s1", ToMs(BaseUtc) - 1000, ToMs(BaseUtc) + 1000, maxPoints: 10);

        Assert.NotNull(series);
        Assert.Empty(series!.Points);
    }

    [Fact]
    public void OneMinuteBuckets_are_pruned_once_they_pass_the_1m_retention_window()
    {
        var closeMinuteZero = BaseUtc.AddSeconds(65);
        Store.RecordSamples(new[] { Row("s1", 40, BaseUtc) }, BaseUtc);
        Store.RecordSamples(new[] { Row("s1", 41, closeMinuteZero) }, closeMinuteZero);

        var farLater = BaseUtc.AddMinutes(AiHistoryRetention.OneMinuteTierRetentionMinutes + 5);
        Store.RecordSamples(new[] { Row("s1", 42, farLater) }, farLater);

        var series = Store.QuerySensorHistory("s1", ToMs(BaseUtc) - 1000, ToMs(BaseUtc) + 60 * 60_000L, maxPoints: 100);

        Assert.NotNull(series);
        Assert.Equal(AiHistoryTier.OneMinute, series!.Tier);
        Assert.Empty(series!.Points);
    }

    [Fact]
    public void Summarize_reports_min_max_avg_latest_and_sample_count_per_sensor()
    {
        Store.RecordSamples(new[] { Row("cpu-temp", 40, BaseUtc, name: "CPU Temperature") }, BaseUtc);
        Store.RecordSamples(new[] { Row("cpu-temp", 60, BaseUtc.AddSeconds(5), name: "CPU Temperature") }, BaseUtc.AddSeconds(5));
        Store.RecordSamples(new[] { Row("cpu-temp", 50, BaseUtc.AddSeconds(10), name: "CPU Temperature") }, BaseUtc.AddSeconds(10));

        var rows = Store.Summarize(ToMs(BaseUtc) - 1000, ToMs(BaseUtc) + 20_000);

        var row = Assert.Single(rows);
        Assert.Equal("cpu-temp", row.SensorId);
        Assert.Equal(40, row.Min);
        Assert.Equal(60, row.Max);
        Assert.Equal(50, row.Avg, precision: 6);
        Assert.Equal(50, row.Latest);
        Assert.Equal(ToMs(BaseUtc.AddSeconds(10)), row.LatestAtUtcMs);
        Assert.Equal(3, row.Samples);
    }

    [Fact]
    public void Summarize_only_includes_sensors_with_data_in_the_window()
    {
        Store.RecordSamples(new[] { Row("s1", 1, BaseUtc) }, BaseUtc);

        var rows = Store.Summarize(ToMs(BaseUtc.AddHours(2)), ToMs(BaseUtc.AddHours(3)));

        Assert.Empty(rows);
    }

    [Fact]
    public void QuerySensorHistory_thins_a_large_raw_series_to_around_maxPoints_preserving_the_edges()
    {
        for (var i = 0; i < 50; i++)
        {
            var t = BaseUtc.AddSeconds(i);
            Store.RecordSamples(new[] { Row("s1", i, t) }, t);
        }

        var series = Store.QuerySensorHistory("s1", ToMs(BaseUtc) - 1000, ToMs(BaseUtc) + 60_000, maxPoints: 10);

        Assert.NotNull(series);
        Assert.True(series!.Points.Count <= 11);
        Assert.Equal(0, series.Points[0].Value);
        Assert.Equal(49, series.Points[^1].Value);
    }

    [Fact]
    public void QueryEvents_returns_newest_first_filters_by_type_and_reports_truncated()
    {
        for (var i = 0; i < 5; i++)
        {
            var kind = i % 2 == 0 ? "ai_write" : "lifecycle";
            Store.RecordEvent(new AiHistoryEventRow(ToMs(BaseUtc.AddSeconds(i)), kind, $"evt{i}", "{}", true, null));
        }

        var all = Store.QueryEvents(0, null, limit: 10);
        Assert.False(all.Truncated);
        Assert.Equal(5, all.Events.Count);
        Assert.Equal("evt4", all.Events[0].Name);
        Assert.Equal("evt0", all.Events[^1].Name);

        var writesOnly = Store.QueryEvents(0, "ai_write", limit: 10);
        Assert.Equal(3, writesOnly.Events.Count);
        Assert.All(writesOnly.Events, e => Assert.Equal("ai_write", e.Kind));

        var capped = Store.QueryEvents(0, null, limit: 2);
        Assert.True(capped.Truncated);
        Assert.Equal(2, capped.Events.Count);
    }

    [Fact]
    public void QueryEvents_round_trips_failure_and_error_text()
    {
        Store.RecordEvent(new AiHistoryEventRow(ToMs(BaseUtc), "ai_write", "apply_cooling_preset", "{\"preset\":\"turbo\"}", false, "boom"));

        var result = Store.QueryEvents(0, null, limit: 10);

        var e = Assert.Single(result.Events);
        Assert.False(e.Success);
        Assert.Equal("boom", e.ErrorText);
        Assert.Equal("{\"preset\":\"turbo\"}", e.ArgsJson);
    }

    [Fact]
    public void SensorHistoryAndEvents_SurviveReopen()
    {
        Store.RecordSamples(new[] { Row("cpu-temp", 55.5, BaseUtc, name: "CPU Temperature") }, BaseUtc);
        Store.RecordEvent(new AiHistoryEventRow(ToMs(BaseUtc), "ai_write", "apply_cooling_preset", "{}", true, null));

        Reopen();

        var series = Store.QuerySensorHistory("cpu-temp", ToMs(BaseUtc) - 1000, ToMs(BaseUtc) + 1000, maxPoints: 10);
        Assert.NotNull(series);
        var point = Assert.Single(series!.Points);
        Assert.Equal(55.5, point.Value);

        var events = Store.QueryEvents(0, null, limit: 10);
        var e = Assert.Single(events.Events);
        Assert.Equal("apply_cooling_preset", e.Name);
    }
}

public sealed class BinaryAiHistoryStoreSpecTests : AiHistoryStoreSpec
{
    protected override IAiHistoryStore CreateStore(string dir) =>
        new BinaryAiHistoryStore(dir);
}
