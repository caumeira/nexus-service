using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Mcp;
using Xunit;

namespace Nexus.Service.Tests.Mcp;

/// <summary>
/// A tool that throws must not surface as a raw HTTP 500: McpServerHost wraps
/// it as a JSON-RPC -32603 error and McpToolRegistry still records the audit
/// row for a non-read-only tool. Own host per test (mirrors
/// McpServerHostLifecycleTests) since this needs a throwing fake tool instead
/// of the real day-one tools McpServerHostIntegrationTests wires up.
/// </summary>
public sealed class McpToolExceptionWireTests : IDisposable
{
    private const string Token = "throw-test-token-0123456789abcdef";
    private readonly TempDir _tempDir = new();
    private McpServerHost? _host;

    public void Dispose()
    {
        try { _host?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        _tempDir.Dispose();
    }

    private sealed class ThrowingTool : IMcpTool
    {
        public string Name => "throwing_tool";
        public string Title => "Throwing Tool";
        public string Description => "A tool that always throws, for exception-path tests.";
        public McpCapability Capability => McpCapability.Cooling;
        public bool ReadOnly => false;
        public string InputSchemaJson => "{\"type\":\"object\",\"properties\":{}}";

        public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class RecordingAuditSink : IMcpAuditSink
    {
        public List<McpAuditEntry> Entries { get; } = new();
        public void Record(McpAuditEntry entry) => Entries.Add(entry);
    }

    [Fact]
    public async Task A_throwing_write_tool_returns_json_rpc_internal_error_not_a_500_and_is_audited()
    {
        var store = new TestableConfigStore(Path.Combine(_tempDir.Root, "settings.json"));
        store.Update(s =>
        {
            s.AiIntegration.Enabled = true;
            s.AiIntegration.Token = Token;
            s.AiIntegration.Port = 0;
        });
        var audit = new RecordingAuditSink();
        var registry = new McpToolRegistry(new IMcpTool[] { new ThrowingTool() }, store, audit);
        var host = new McpServerHost(store, registry);
        _host = host;
        await host.ApplyConfiguredStateAsync();
        Assert.True(host.Running);

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.BoundPort}/") };
        var req = new HttpRequestMessage(HttpMethod.Post, "mcp")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tools/call",
                @params = new { name = "throwing_tool", arguments = (object?)null },
            }), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal(-32603, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());

        var entry = Assert.Single(audit.Entries);
        Assert.False(entry.Success);
        Assert.Contains("boom", entry.ErrorText, StringComparison.Ordinal);
    }
}
