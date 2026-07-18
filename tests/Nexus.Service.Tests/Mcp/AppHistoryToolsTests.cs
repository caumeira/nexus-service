using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Mcp;
using Nexus.Service.Mcp.Tools;
using Nexus.Service.Monitoring.History;
using Xunit;

namespace Nexus.Service.Tests.Mcp;

/// <summary>
/// get_top_apps and query_app_history: metric-id validation (bare and
/// :&lt;gpuId&gt; suffix forms), the happy paths against
/// InMemoryMetricsHistoryStore's IAppUsageHistoryStore side, and
/// McpCapability.History consent gating end to end through McpToolRegistry.
/// </summary>
public sealed class AppHistoryToolsTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "nexus-mcp-app-history-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private TestableConfigStore NewConfigStore() => new(Path.Combine(_tempDir, "settings.json"));

    // ── get_top_apps ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetTopApps_missing_metric_is_error()
    {
        var tool = new GetTopAppsTool(new InMemoryMetricsHistoryStore());

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { minutes = 10 }), CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task GetTopApps_unknown_metric_is_error()
    {
        var tool = new GetTopAppsTool(new InMemoryMetricsHistoryStore());

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { metric = "bogus", minutes = 10 }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("cpu", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetTopApps_missing_minutes_is_error()
    {
        var tool = new GetTopAppsTool(new InMemoryMetricsHistoryStore());

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { metric = "cpu" }), CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task GetTopApps_happy_path_ranks_apps_by_average_descending_and_respects_limit()
    {
        var store = new InMemoryMetricsHistoryStore();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        store.Append(new[]
        {
            new AppUsageTick(now, new[]
            {
                new AppMetricSample("cpu", new[] { new AppUsagePoint("chrome.exe", 60, null), new AppUsagePoint("code.exe", 20, null) }),
            }),
        }, null);
        var tool = new GetTopAppsTool(store);

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { metric = "cpu", minutes = 10, limit = 1 }), CancellationToken.None);

        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Text);
        Assert.Equal("cpu", doc.RootElement.GetProperty("metric").GetString());
        var app = Assert.Single(doc.RootElement.GetProperty("apps").EnumerateArray());
        Assert.Equal("chrome.exe", app.GetProperty("name").GetString());
        Assert.Equal(60, app.GetProperty("avg").GetDouble());
    }

    [Fact]
    public async Task GetTopApps_gpu_adapter_suffix_is_a_valid_metric()
    {
        var store = new InMemoryMetricsHistoryStore();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        store.Append(new[]
        {
            new AppUsageTick(now, new[] { new AppMetricSample("gpu:gpu-0", new[] { new AppUsagePoint("game.exe", 80, 2048) }) }),
        }, null);
        var tool = new GetTopAppsTool(store);

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { metric = "gpu:gpu-0", minutes = 10 }), CancellationToken.None);

        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Text);
        var app = Assert.Single(doc.RootElement.GetProperty("apps").EnumerateArray());
        Assert.Equal("game.exe", app.GetProperty("name").GetString());
    }

    [Fact]
    public async Task GetTopApps_consent_disabled_is_refused_by_the_registry_without_executing()
    {
        var configStore = NewConfigStore();
        configStore.Update(s => s.AiIntegration.AllowHistory = false);
        var tool = new GetTopAppsTool(new InMemoryMetricsHistoryStore());
        var registry = new McpToolRegistry(new IMcpTool[] { tool }, configStore, new LoggingMcpAuditSink());

        var result = await registry.CallAsync(
            tool, JsonSerializer.SerializeToElement(new { metric = "cpu", minutes = 10 }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("Allow history access", result.Text, StringComparison.Ordinal);
    }

    // ── query_app_history ────────────────────────────────────────────────────

    [Fact]
    public async Task QueryAppHistory_missing_metric_is_error()
    {
        var tool = new QueryAppHistoryTool(new InMemoryMetricsHistoryStore());

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { app = "chrome.exe", minutes = 10 }), CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task QueryAppHistory_unknown_metric_is_error()
    {
        var tool = new QueryAppHistoryTool(new InMemoryMetricsHistoryStore());

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { metric = "bogus", app = "chrome.exe", minutes = 10 }), CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task QueryAppHistory_missing_app_is_error()
    {
        var tool = new QueryAppHistoryTool(new InMemoryMetricsHistoryStore());

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { metric = "cpu", minutes = 10 }), CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task QueryAppHistory_missing_minutes_is_error()
    {
        var tool = new QueryAppHistoryTool(new InMemoryMetricsHistoryStore());

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { metric = "cpu", app = "chrome.exe" }), CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task QueryAppHistory_happy_path_returns_points()
    {
        var store = new InMemoryMetricsHistoryStore();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        store.Append(new[]
        {
            new AppUsageTick(now, new[] { new AppMetricSample("cpu", new[] { new AppUsagePoint("chrome.exe", 55, null) }) }),
        }, null);
        var tool = new QueryAppHistoryTool(store);

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { metric = "cpu", app = "chrome.exe", minutes = 10 }), CancellationToken.None);

        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Text);
        Assert.Equal("chrome.exe", doc.RootElement.GetProperty("app").GetString());
        var point = Assert.Single(doc.RootElement.GetProperty("points").EnumerateArray());
        Assert.Equal(55, point.GetProperty("value").GetDouble());
        Assert.False(doc.RootElement.TryGetProperty("vramAvgMb", out _));
    }

    [Fact]
    public async Task QueryAppHistory_gpu_metric_reports_window_average_vram()
    {
        var store = new InMemoryMetricsHistoryStore();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        store.Append(new[]
        {
            new AppUsageTick(now, new[] { new AppMetricSample("gpu:gpu-0", new[] { new AppUsagePoint("game.exe", 80, 2048) }) }),
        }, null);
        var tool = new QueryAppHistoryTool(store);

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { metric = "gpu:gpu-0", app = "game.exe", minutes = 10 }), CancellationToken.None);

        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Text);
        Assert.Equal(2048, doc.RootElement.GetProperty("vramAvgMb").GetDouble());
    }

    [Fact]
    public async Task QueryAppHistory_consent_disabled_is_refused_by_the_registry_without_executing()
    {
        var configStore = NewConfigStore();
        configStore.Update(s => s.AiIntegration.AllowHistory = false);
        var tool = new QueryAppHistoryTool(new InMemoryMetricsHistoryStore());
        var registry = new McpToolRegistry(new IMcpTool[] { tool }, configStore, new LoggingMcpAuditSink());

        var result = await registry.CallAsync(
            tool, JsonSerializer.SerializeToElement(new { metric = "cpu", app = "chrome.exe", minutes = 10 }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("Allow history access", result.Text, StringComparison.Ordinal);
    }
}
