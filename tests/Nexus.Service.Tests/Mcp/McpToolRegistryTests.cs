using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Mcp;
using Xunit;

namespace Nexus.Service.Tests.Mcp;

/// <summary>
/// McpToolRegistry.CallAsync in isolation: live consent gate and the audit
/// sink contract (write-tool calls and refusals are recorded; read-only tool
/// calls never are). Uses a fake IMcpTool rather than a real write tool since
/// none ship yet - this pins the seam the write-tools follow-up task builds on.
/// </summary>
public sealed class McpToolRegistryTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "nexus-mcp-registry-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private TestableConfigStore NewStore() => new(Path.Combine(_tempDir, "settings.json"));

    private sealed class FakeAuditSink : IMcpAuditSink
    {
        public List<McpAuditEntry> Entries { get; } = new();
        public void Record(McpAuditEntry entry) => Entries.Add(entry);
    }

    private sealed class FakeTool : IMcpTool
    {
        public string Name => "fake_tool";
        public string Title => "Fake Tool";
        public string Description => "A fake tool for registry tests.";
        public McpCapability Capability { get; init; } = McpCapability.Telemetry;
        public bool ReadOnly { get; init; } = true;
        public string InputSchemaJson => "{\"type\":\"object\",\"properties\":{}}";
        public Func<JsonElement?, McpToolExecutionResult>? Behavior { get; init; }
        public int CallCount { get; private set; }

        public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
        {
            CallCount++;
            var result = Behavior?.Invoke(args) ?? McpToolExecutionResult.Ok("{}");
            return Task.FromResult(result);
        }
    }

    [Fact]
    public async Task CallAsync_consent_disabled_returns_error_naming_the_toggle_and_does_not_execute()
    {
        var store = NewStore();
        store.Update(s => s.AiIntegration.AllowTelemetry = false);
        var tool = new FakeTool { Capability = McpCapability.Telemetry, ReadOnly = true };
        var registry = new McpToolRegistry(new IMcpTool[] { tool }, store, new FakeAuditSink());

        var result = await registry.CallAsync(tool, null, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("Allow telemetry", result.Text, StringComparison.Ordinal);
        Assert.Equal(0, tool.CallCount);
    }

    [Fact]
    public async Task CallAsync_consent_enabled_executes_the_tool_and_returns_its_result()
    {
        var store = NewStore();
        var tool = new FakeTool
        {
            Capability = McpCapability.Telemetry,
            ReadOnly = true,
            Behavior = _ => McpToolExecutionResult.Ok("{\"ok\":true}"),
        };
        var registry = new McpToolRegistry(new IMcpTool[] { tool }, store, new FakeAuditSink());

        var result = await registry.CallAsync(tool, null, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("{\"ok\":true}", result.Text);
        Assert.Equal(1, tool.CallCount);
    }

    [Fact]
    public async Task CallAsync_read_only_tool_is_never_audited_success_or_refusal()
    {
        var store = NewStore();
        store.Update(s => s.AiIntegration.AllowCooling = false);
        var audit = new FakeAuditSink();
        var telemetryTool = new FakeTool { Capability = McpCapability.Telemetry, ReadOnly = true };
        var coolingTool = new FakeTool { Capability = McpCapability.Cooling, ReadOnly = true };
        var registry = new McpToolRegistry(new IMcpTool[] { telemetryTool, coolingTool }, store, audit);

        await registry.CallAsync(telemetryTool, null, CancellationToken.None);
        await registry.CallAsync(coolingTool, null, CancellationToken.None);

        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task CallAsync_non_read_only_tool_audits_success_then_refusal()
    {
        var store = NewStore();
        var audit = new FakeAuditSink();
        var tool = new FakeTool
        {
            Capability = McpCapability.Cooling,
            ReadOnly = false,
            Behavior = _ => McpToolExecutionResult.Ok("done"),
        };
        var registry = new McpToolRegistry(new IMcpTool[] { tool }, store, audit);

        await registry.CallAsync(tool, null, CancellationToken.None);
        Assert.Single(audit.Entries);
        Assert.True(audit.Entries[0].Success);
        Assert.Null(audit.Entries[0].ErrorText);

        store.Update(s => s.AiIntegration.AllowCooling = false);
        await registry.CallAsync(tool, null, CancellationToken.None);

        Assert.Equal(2, audit.Entries.Count);
        Assert.False(audit.Entries[1].Success);
        Assert.NotNull(audit.Entries[1].ErrorText);
    }

    [Fact]
    public async Task CallAsync_non_read_only_tool_audits_its_own_reported_error()
    {
        var store = NewStore();
        var audit = new FakeAuditSink();
        var tool = new FakeTool
        {
            Capability = McpCapability.Lighting,
            ReadOnly = false,
            Behavior = _ => McpToolExecutionResult.Error("bad input"),
        };
        var registry = new McpToolRegistry(new IMcpTool[] { tool }, store, audit);

        var result = await registry.CallAsync(tool, null, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Single(audit.Entries);
        Assert.False(audit.Entries[0].Success);
        Assert.Equal("bad input", audit.Entries[0].ErrorText);
    }

    [Fact]
    public async Task CallAsync_non_read_only_tool_that_throws_audits_failure_and_throws_internal_error()
    {
        var store = NewStore();
        var audit = new FakeAuditSink();
        var tool = new FakeTool
        {
            Capability = McpCapability.Cooling,
            ReadOnly = false,
            Behavior = _ => throw new InvalidOperationException("boom"),
        };
        var registry = new McpToolRegistry(new IMcpTool[] { tool }, store, audit);

        await Assert.ThrowsAsync<McpToolExecutionException>(
            () => registry.CallAsync(tool, null, CancellationToken.None));

        var entry = Assert.Single(audit.Entries);
        Assert.False(entry.Success);
        Assert.Contains("boom", entry.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallAsync_read_only_tool_that_throws_is_not_audited_but_still_throws_internal_error()
    {
        var store = NewStore();
        var audit = new FakeAuditSink();
        var tool = new FakeTool
        {
            Capability = McpCapability.Telemetry,
            ReadOnly = true,
            Behavior = _ => throw new InvalidOperationException("boom"),
        };
        var registry = new McpToolRegistry(new IMcpTool[] { tool }, store, audit);

        await Assert.ThrowsAsync<McpToolExecutionException>(
            () => registry.CallAsync(tool, null, CancellationToken.None));

        Assert.Empty(audit.Entries);
    }

    [Fact]
    public void TryGetTool_unknown_name_returns_false()
    {
        var store = NewStore();
        var registry = new McpToolRegistry(Array.Empty<IMcpTool>(), store, new FakeAuditSink());

        Assert.False(registry.TryGetTool("does_not_exist", out _));
    }
}
