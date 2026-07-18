using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Mcp.History;
using Nexus.Service.Mcp.History.Binary;
using Nexus.Service.Mcp.Tools;
using Xunit;

namespace Nexus.Service.Tests.Mcp;

/// <summary>
/// Unit coverage for the three history MCP tools against a real
/// BinaryAiHistoryStore (temp dir, matching AiHistoryStoreTests) for the
/// happy paths, and UnavailableAiHistoryStore for the store-unavailable path
/// every tool must report as isError rather than throw or return empty data.
/// </summary>
public sealed class HistoryToolsTests : IDisposable
{
    private static readonly DateTime BaseUtc = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "nexus-mcp-history-tools-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly BinaryAiHistoryStore _store;

    public HistoryToolsTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new BinaryAiHistoryStore(_dir);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static long ToMs(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();

    // ── query_sensor_history ─────────────────────────────────────────────────

    [Fact]
    public async Task QuerySensorHistory_missing_sensorId_is_error()
    {
        var tool = new QuerySensorHistoryTool(_store);

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { minutes = 10 }), CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task QuerySensorHistory_missing_minutes_is_error()
    {
        var tool = new QuerySensorHistoryTool(_store);

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { sensorId = "cpu-temp" }), CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task QuerySensorHistory_unknown_sensor_is_error_listing_recorded_sensors()
    {
        _store.RecordSamples(new[] { new AiHistorySampleRow("cpu-temp", "CPU Temperature", "temperature", "C", 55, ToMs(BaseUtc)) }, BaseUtc);
        var tool = new QuerySensorHistoryTool(_store);

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { sensorId = "does-not-exist", minutes = 10 }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("cpu-temp", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuerySensorHistory_unknown_sensor_with_nothing_recorded_says_so()
    {
        var tool = new QuerySensorHistoryTool(_store);

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { sensorId = "does-not-exist", minutes = 10 }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("none recorded", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QuerySensorHistory_happy_path_returns_points_tier_and_unit()
    {
        // The tool windows against the real wall clock, so the recorded sample
        // must land inside "now minus minutes" rather than the fixed BaseUtc
        // the tiering/pruning-math tests use.
        var now = DateTime.UtcNow;
        _store.RecordSamples(new[] { new AiHistorySampleRow("cpu-temp", "CPU Temperature", "temperature", "C", 55, ToMs(now)) }, now);
        var tool = new QuerySensorHistoryTool(_store);

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { sensorId = "cpu-temp", minutes = 5 }), CancellationToken.None);

        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Text);
        Assert.Equal("cpu-temp", doc.RootElement.GetProperty("sensorId").GetString());
        Assert.Equal("CPU Temperature", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("C", doc.RootElement.GetProperty("unit").GetString());
        Assert.Equal("raw", doc.RootElement.GetProperty("tier").GetString());
        var points = doc.RootElement.GetProperty("points").EnumerateArray();
        Assert.Single(points);
    }

    [Fact]
    public async Task QuerySensorHistory_when_store_unavailable_is_error()
    {
        var tool = new QuerySensorHistoryTool(new UnavailableAiHistoryStore());

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { sensorId = "cpu-temp", minutes = 5 }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("unavailable", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    // ── get_history_summary ──────────────────────────────────────────────────

    [Fact]
    public async Task GetHistorySummary_missing_minutes_is_error()
    {
        var tool = new GetHistorySummaryTool(_store);

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { }), CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task GetHistorySummary_happy_path_reports_min_max_avg_latest()
    {
        // Both timestamps must be at or before the tool's own DateTimeOffset.UtcNow
        // capture, or its window's upper bound excludes a row that claims a time
        // later than the real clock has actually reached.
        var now = DateTime.UtcNow;
        var t0 = now.AddSeconds(-2);
        var t1 = now.AddSeconds(-1);
        _store.RecordSamples(new[] { new AiHistorySampleRow("cpu-temp", "CPU Temperature", "temperature", "C", 40, ToMs(t0)) }, t0);
        _store.RecordSamples(new[] { new AiHistorySampleRow("cpu-temp", "CPU Temperature", "temperature", "C", 60, ToMs(t1)) }, t1);
        var tool = new GetHistorySummaryTool(_store);

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { minutes = 5 }), CancellationToken.None);

        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Text);
        Assert.Equal(5, doc.RootElement.GetProperty("minutes").GetInt32());
        var sensor = Assert.Single(doc.RootElement.GetProperty("sensors").EnumerateArray());
        Assert.Equal("cpu-temp", sensor.GetProperty("sensorId").GetString());
        Assert.Equal(40, sensor.GetProperty("min").GetDouble());
        Assert.Equal(60, sensor.GetProperty("max").GetDouble());
        Assert.Equal(60, sensor.GetProperty("latest").GetDouble());
        Assert.Equal(2, sensor.GetProperty("samples").GetInt32());
    }

    [Fact]
    public async Task GetHistorySummary_when_store_unavailable_is_error()
    {
        var tool = new GetHistorySummaryTool(new UnavailableAiHistoryStore());

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { minutes = 5 }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("unavailable", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    // ── query_events ──────────────────────────────────────────────────────────

    [Fact]
    public async Task QueryEvents_missing_minutes_is_error()
    {
        var tool = new QueryEventsTool(_store);

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { }), CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task QueryEvents_invalid_type_is_error()
    {
        var tool = new QueryEventsTool(_store);

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { minutes = 5, type = "not_a_type" }), CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task QueryEvents_happy_path_returns_newest_first_and_respects_limit()
    {
        // Offsets must stay at or before "now" - the tool windows against the
        // real wall clock, which would exclude a row timestamped later than it
        // has actually reached (see GetHistorySummary_happy_path's note).
        var now = DateTime.UtcNow;
        for (var i = 0; i < 3; i++)
        {
            _store.RecordEvent(new AiHistoryEventRow(ToMs(now.AddSeconds(i - 3)), "ai_write", $"tool{i}", "{}", true, null));
        }
        var tool = new QueryEventsTool(_store);

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { minutes = 5, limit = 2 }), CancellationToken.None);

        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Text);
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
        var events = doc.RootElement.GetProperty("events").EnumerateArray();
        var first = Assert.Single(events, e => e.GetProperty("name").GetString() == "tool2");
        Assert.Equal("ai_write", first.GetProperty("type").GetString());
        Assert.True(first.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task QueryEvents_when_store_unavailable_is_error()
    {
        var tool = new QueryEventsTool(new UnavailableAiHistoryStore());

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { minutes = 5 }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("unavailable", result.Text, StringComparison.OrdinalIgnoreCase);
    }
}
