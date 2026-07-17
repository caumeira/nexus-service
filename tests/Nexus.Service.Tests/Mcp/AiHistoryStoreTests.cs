using System;
using System.IO;
using Nexus.Service.Mcp.History;
using Xunit;

namespace Nexus.Service.Tests.Mcp;

/// <summary>
/// SqliteAiHistoryStore against a temp-dir SQLite file: tier selection by
/// window length, raw-to-1m-to-5m rollup on write, per-tier pruning, summary
/// math, event insert/query, and point thinning. Timestamps are synthetic
/// (a fixed epoch stepped by hand) so the tiering/rollup math is deterministic
/// and does not depend on wall-clock time.
/// </summary>
public sealed class AiHistoryStoreTests : IDisposable
{
    private static readonly DateTime BaseUtc = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "nexus-ai-history-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly SqliteAiHistoryStore _store;

    public AiHistoryStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new SqliteAiHistoryStore(Path.Combine(_dir, "ai-history.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static long ToMs(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();

    private static AiHistorySampleRow Row(string sensorId, double value, DateTime t,
        string name = "Sensor", string kind = "temperature", string unit = "C") =>
        new(sensorId, name, kind, unit, value, ToMs(t));

    // ── tier selection (pure function) ──────────────────────────────────────

    [Theory]
    [InlineData(1, AiHistoryTier.Raw)]
    [InlineData(30, AiHistoryTier.Raw)]
    [InlineData(31, AiHistoryTier.OneMinute)]
    [InlineData(1440, AiHistoryTier.OneMinute)]
    [InlineData(1441, AiHistoryTier.FiveMinute)]
    [InlineData(10080, AiHistoryTier.FiveMinute)]
    public void PickTier_selects_by_window_length(int minutes, AiHistoryTier expected)
    {
        Assert.Equal(expected, AiHistoryRetention.PickTier(minutes));
    }

    // ── raw insert + query ───────────────────────────────────────────────────

    [Fact]
    public void RecordSamples_then_QuerySensorHistory_round_trips_a_raw_point()
    {
        _store.RecordSamples(new[] { Row("cpu-temp", 55.5, BaseUtc, name: "CPU Temperature") }, BaseUtc);

        var series = _store.QuerySensorHistory("cpu-temp", ToMs(BaseUtc) - 1000, ToMs(BaseUtc) + 1000, maxPoints: 10);

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
        Assert.Null(_store.QuerySensorHistory("does-not-exist", 0, ToMs(BaseUtc), maxPoints: 10));
    }

    [Fact]
    public void KnownSensorIds_lists_every_sensor_ever_recorded()
    {
        _store.RecordSamples(new[] { Row("cpu-temp", 40, BaseUtc), Row("gpu-temp", 50, BaseUtc) }, BaseUtc);

        var ids = _store.KnownSensorIds();

        Assert.Equal(new[] { "cpu-temp", "gpu-temp" }, ids);
    }

    // ── rollup ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_completed_minute_bucket_rolls_up_into_the_1m_tier_as_an_average()
    {
        for (var i = 0; i < 12; i++)
        {
            var t = BaseUtc.AddSeconds(i * 5); // 0, 5, ..., 55 - all inside minute 0
            _store.RecordSamples(new[] { Row("cpu-temp", 40 + i, t) }, t);
        }
        // Crossing into minute 1 closes minute 0's bucket and triggers its rollup.
        var trigger = BaseUtc.AddSeconds(65);
        _store.RecordSamples(new[] { Row("cpu-temp", 99, trigger) }, trigger);

        // A 40-minute window forces the 1-minute tier.
        var series = _store.QuerySensorHistory("cpu-temp", ToMs(BaseUtc), ToMs(BaseUtc) + 40 * 60_000L, maxPoints: 100);

        Assert.NotNull(series);
        Assert.Equal(AiHistoryTier.OneMinute, series!.Tier);
        var bucket = Assert.Single(series.Points, p => p.TUtcMs == ToMs(BaseUtc));
        Assert.Equal(45.5, bucket.Value); // average of 40..51
    }

    [Fact]
    public void A_completed_five_minute_span_of_1m_buckets_rolls_up_into_the_5m_tier()
    {
        // One raw sample per minute for 6 minutes closes five 1-minute buckets
        // (0..4) and crosses into the 5-minute boundary, rolling them into 5m.
        for (var i = 0; i < 6; i++)
        {
            var t = BaseUtc.AddMinutes(i).AddSeconds(1);
            _store.RecordSamples(new[] { Row("cpu-temp", 10 + i, t) }, t);
        }

        var series = _store.QuerySensorHistory("cpu-temp", ToMs(BaseUtc), ToMs(BaseUtc) + 2000 * 60_000L, maxPoints: 100);

        Assert.NotNull(series);
        Assert.Equal(AiHistoryTier.FiveMinute, series!.Tier);
        var bucket = Assert.Single(series.Points, p => p.TUtcMs == ToMs(BaseUtc));
        Assert.Equal(12.0, bucket.Value, precision: 6); // average of 10,11,12,13,14
    }

    // ── pruning ──────────────────────────────────────────────────────────────

    [Fact]
    public void Raw_rows_are_pruned_once_they_pass_the_raw_retention_window()
    {
        _store.RecordSamples(new[] { Row("s1", 40, BaseUtc) }, BaseUtc);

        var later = BaseUtc.AddMinutes(AiHistoryRetention.RawRetentionMinutes + 1);
        _store.RecordSamples(new[] { Row("s1", 41, later) }, later);

        var series = _store.QuerySensorHistory("s1", ToMs(BaseUtc) - 1000, ToMs(BaseUtc) + 1000, maxPoints: 10);

        Assert.NotNull(series); // sensor_meta keeps the sensor known
        Assert.Empty(series!.Points);
    }

    [Fact]
    public void OneMinuteBuckets_are_pruned_once_they_pass_the_1m_retention_window()
    {
        var closeMinuteZero = BaseUtc.AddSeconds(65);
        _store.RecordSamples(new[] { Row("s1", 40, BaseUtc) }, BaseUtc);
        _store.RecordSamples(new[] { Row("s1", 41, closeMinuteZero) }, closeMinuteZero);

        var farLater = BaseUtc.AddMinutes(AiHistoryRetention.OneMinuteTierRetentionMinutes + 5);
        _store.RecordSamples(new[] { Row("s1", 42, farLater) }, farLater);

        var series = _store.QuerySensorHistory("s1", ToMs(BaseUtc) - 1000, ToMs(BaseUtc) + 60 * 60_000L, maxPoints: 100);

        Assert.NotNull(series);
        Assert.Equal(AiHistoryTier.OneMinute, series!.Tier);
        Assert.Empty(series!.Points);
    }

    // ── summary ──────────────────────────────────────────────────────────────

    [Fact]
    public void Summarize_reports_min_max_avg_latest_and_sample_count_per_sensor()
    {
        _store.RecordSamples(new[]
        {
            Row("cpu-temp", 40, BaseUtc, name: "CPU Temperature"),
        }, BaseUtc);
        _store.RecordSamples(new[]
        {
            Row("cpu-temp", 60, BaseUtc.AddSeconds(5), name: "CPU Temperature"),
        }, BaseUtc.AddSeconds(5));
        _store.RecordSamples(new[]
        {
            Row("cpu-temp", 50, BaseUtc.AddSeconds(10), name: "CPU Temperature"),
        }, BaseUtc.AddSeconds(10));

        var rows = _store.Summarize(ToMs(BaseUtc) - 1000, ToMs(BaseUtc) + 20_000);

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
        _store.RecordSamples(new[] { Row("s1", 1, BaseUtc) }, BaseUtc);

        var rows = _store.Summarize(ToMs(BaseUtc.AddHours(2)), ToMs(BaseUtc.AddHours(3)));

        Assert.Empty(rows);
    }

    // ── thinning ─────────────────────────────────────────────────────────────

    [Fact]
    public void QuerySensorHistory_thins_a_large_raw_series_to_around_maxPoints_preserving_the_edges()
    {
        for (var i = 0; i < 50; i++)
        {
            var t = BaseUtc.AddSeconds(i);
            _store.RecordSamples(new[] { Row("s1", i, t) }, t);
        }

        var series = _store.QuerySensorHistory("s1", ToMs(BaseUtc) - 1000, ToMs(BaseUtc) + 60_000, maxPoints: 10);

        Assert.NotNull(series);
        Assert.True(series!.Points.Count <= 11);
        Assert.Equal(0, series.Points[0].Value);
        Assert.Equal(49, series.Points[^1].Value);
    }

    // ── events ───────────────────────────────────────────────────────────────

    [Fact]
    public void QueryEvents_returns_newest_first_filters_by_type_and_reports_truncated()
    {
        for (var i = 0; i < 5; i++)
        {
            var kind = i % 2 == 0 ? "ai_write" : "lifecycle";
            _store.RecordEvent(new AiHistoryEventRow(ToMs(BaseUtc.AddSeconds(i)), kind, $"evt{i}", "{}", true, null));
        }

        var all = _store.QueryEvents(0, null, limit: 10);
        Assert.False(all.Truncated);
        Assert.Equal(5, all.Events.Count);
        Assert.Equal("evt4", all.Events[0].Name);
        Assert.Equal("evt0", all.Events[^1].Name);

        var writesOnly = _store.QueryEvents(0, "ai_write", limit: 10);
        Assert.Equal(3, writesOnly.Events.Count);
        Assert.All(writesOnly.Events, e => Assert.Equal("ai_write", e.Kind));

        var capped = _store.QueryEvents(0, null, limit: 2);
        Assert.True(capped.Truncated);
        Assert.Equal(2, capped.Events.Count);
    }

    [Fact]
    public void QueryEvents_round_trips_failure_and_error_text()
    {
        _store.RecordEvent(new AiHistoryEventRow(ToMs(BaseUtc), "ai_write", "apply_cooling_preset", "{\"preset\":\"turbo\"}", false, "boom"));

        var result = _store.QueryEvents(0, null, limit: 10);

        var e = Assert.Single(result.Events);
        Assert.False(e.Success);
        Assert.Equal("boom", e.ErrorText);
        Assert.Equal("{\"preset\":\"turbo\"}", e.ArgsJson);
    }
}
