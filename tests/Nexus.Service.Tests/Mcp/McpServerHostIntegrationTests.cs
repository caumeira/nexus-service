using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Nexus.Service.Mcp;
using Xunit;

namespace Nexus.Service.Tests.Mcp;

/// <summary>
/// Drives the real McpServerHost listener over HTTP on an ephemeral loopback
/// port, against a temp-file config store and the real day-one tools (wired
/// to stub hardware providers). One fresh host per test (xUnit constructs a
/// new instance per [Fact]), enabled from the start with a known token.
/// </summary>
public sealed class McpServerHostIntegrationTests : IAsyncLifetime
{
    private const string Token = "test-mcp-token-0123456789abcdef";

    private TempDir _tempDir = null!;
    private TestableConfigStore _store = null!;
    private McpServerHost _host = null!;
    private HttpClient _client = null!;
    private McpTestHarness.StubSensorProvider _sensors = null!;

    public async Task InitializeAsync()
    {
        _tempDir = new TempDir();
        _store = new TestableConfigStore(Path.Combine(_tempDir.Root, "settings.json"));
        _store.Update(s =>
        {
            s.AiIntegration.Enabled = true;
            s.AiIntegration.Token = Token;
            s.AiIntegration.Port = 0;
        });
        var tools = McpTestHarness.BuildRealTools(_store, out _sensors, out _);
        var registry = new McpToolRegistry(tools, _store, new LoggingMcpAuditSink());
        _host = new McpServerHost(_store, registry);
        await _host.ApplyConfiguredStateAsync();
        Assert.True(_host.Running);
        Assert.NotNull(_host.BoundPort);
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_host.BoundPort}/") };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.DisposeAsync();
        _tempDir.Dispose();
    }

    private HttpRequestMessage Request(object body, string? bearerToken = Token, string? origin = null, string? protocolVersion = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "mcp")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        if (bearerToken is not null)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }
        if (origin is not null)
        {
            req.Headers.Add("Origin", origin);
        }
        if (protocolVersion is not null)
        {
            req.Headers.Add("MCP-Protocol-Version", protocolVersion);
        }
        return req;
    }

    private static object Initialize(object id, string? protocolVersion = null) => new
    {
        jsonrpc = "2.0",
        id,
        method = "initialize",
        @params = new { protocolVersion },
    };

    private static object ToolsCall(object id, string name, object? arguments = null) => new
    {
        jsonrpc = "2.0",
        id,
        method = "tools/call",
        @params = new { name, arguments },
    };

    [Fact]
    public async Task Origin_absent_is_allowed()
    {
        var res = await _client.SendAsync(Request(Initialize(1)));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Origin_loopback_is_allowed()
    {
        var res = await _client.SendAsync(Request(Initialize(1), origin: $"http://127.0.0.1:{_host.BoundPort}"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Origin_non_loopback_is_403()
    {
        var res = await _client.SendAsync(Request(Initialize(1), origin: "https://evil.example"));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Missing_token_is_401()
    {
        var res = await _client.SendAsync(Request(Initialize(1), bearerToken: null));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Wrong_token_is_401()
    {
        var res = await _client.SendAsync(Request(Initialize(1), bearerToken: "not-the-token"));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Unsupported_protocol_version_header_is_400()
    {
        var res = await _client.SendAsync(Request(Initialize(1), protocolVersion: "1999-01-01"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Supported_protocol_version_header_is_accepted()
    {
        var res = await _client.SendAsync(Request(Initialize(1), protocolVersion: "2025-06-18"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Initialize_echoes_a_supported_requested_protocol_version()
    {
        var res = await _client.SendAsync(Request(Initialize(1, "2025-06-18")));
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("2025-06-18", doc.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public async Task Initialize_falls_back_to_the_latest_version_when_unsupported_or_absent()
    {
        var res = await _client.SendAsync(Request(Initialize(1, "2099-01-01")));
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal(McpServerHost.LatestProtocolVersion, doc.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public async Task Get_and_delete_are_405()
    {
        var getRes = await _client.GetAsync("mcp");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, getRes.StatusCode);

        var delRes = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "mcp"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, delRes.StatusCode);
    }

    [Fact]
    public async Task A_json_array_body_is_rejected_with_invalid_request()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "mcp")
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal(-32600, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Notification_gets_202_with_no_body()
    {
        var req = Request(new { jsonrpc = "2.0", method = "notifications/initialized" });
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        Assert.Empty(await res.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Ping_returns_an_empty_result()
    {
        var res = await _client.SendAsync(Request(new { jsonrpc = "2.0", id = 1, method = "ping" }));
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Object, doc.RootElement.GetProperty("result").ValueKind);
        Assert.False(doc.RootElement.GetProperty("result").EnumerateObject().MoveNext());
    }

    [Fact]
    public async Task Unknown_method_is_method_not_found()
    {
        var res = await _client.SendAsync(Request(new { jsonrpc = "2.0", id = 1, method = "resources/list" }));
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal(-32601, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Full_initialize_initialized_tools_list_tools_call_flow_succeeds()
    {
        var initRes = await _client.SendAsync(Request(Initialize("a")));
        Assert.Equal(HttpStatusCode.OK, initRes.StatusCode);
        using (var initDoc = JsonDocument.Parse(await initRes.Content.ReadAsStringAsync()))
        {
            Assert.Equal("a", initDoc.RootElement.GetProperty("id").GetString());
            Assert.Equal("nexus-service", initDoc.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
        }

        var initializedRes = await _client.SendAsync(Request(new { jsonrpc = "2.0", method = "notifications/initialized" }));
        Assert.Equal(HttpStatusCode.Accepted, initializedRes.StatusCode);

        var listRes = await _client.SendAsync(Request(new { jsonrpc = "2.0", id = 2, method = "tools/list" }));
        using var listDoc = JsonDocument.Parse(await listRes.Content.ReadAsStringAsync());
        var toolNames = new List<string>();
        foreach (var t in listDoc.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray())
        {
            toolNames.Add(t.GetProperty("name").GetString()!);
        }
        Assert.Contains("get_system_overview", toolNames);
        Assert.Contains("get_sensors", toolNames);
        Assert.Contains("get_cooling_state", toolNames);
        Assert.Contains("get_lighting_state", toolNames);
        Assert.Contains("apply_cooling_preset", toolNames);
        Assert.Contains("set_global_fan_speed", toolNames);
        Assert.Contains("set_fan_curve", toolNames);
        Assert.Contains("apply_lighting_scenario", toolNames);
        Assert.Contains("set_static_color", toolNames);
        Assert.Contains("set_brightness", toolNames);
        Assert.Contains("stop_lighting", toolNames);

        _sensors.Cpu.Add(new Nexus.Service.Models.Sensors.HardwareSensor { Id = "cpu/test", Name = "Test", Type = "Load", Value = 42f });
        var callRes = await _client.SendAsync(Request(ToolsCall(3, "get_system_overview")));
        using var callDoc = JsonDocument.Parse(await callRes.Content.ReadAsStringAsync());
        var result = callDoc.RootElement.GetProperty("result");
        Assert.False(result.GetProperty("isError").GetBoolean());
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("Test CPU", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tools_call_with_unknown_tool_name_is_invalid_params()
    {
        var res = await _client.SendAsync(Request(ToolsCall(1, "does_not_exist")));
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal(-32602, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Consent_disabled_capability_returns_a_tool_result_isError_naming_the_toggle()
    {
        _store.Update(s => s.AiIntegration.AllowTelemetry = false);

        var res = await _client.SendAsync(Request(ToolsCall(1, "get_system_overview")));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var result = doc.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("Allow telemetry", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_sensors_with_an_unknown_device_is_a_tool_result_error_not_a_protocol_error()
    {
        var res = await _client.SendAsync(Request(ToolsCall(1, "get_sensors", new { device = "not-a-device" })));

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("result", out var result));
        Assert.True(result.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task A_write_tool_call_over_the_wire_succeeds_and_reports_readonly_false()
    {
        var listRes = await _client.SendAsync(Request(new { jsonrpc = "2.0", id = 1, method = "tools/list" }));
        using (var listDoc = JsonDocument.Parse(await listRes.Content.ReadAsStringAsync()))
        {
            var tool = listDoc.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
                .Single(t => t.GetProperty("name").GetString() == "apply_cooling_preset");
            Assert.False(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
            Assert.True(tool.GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());
        }

        var callRes = await _client.SendAsync(Request(ToolsCall(2, "apply_cooling_preset", new { preset = "off" })));
        using var callDoc = JsonDocument.Parse(await callRes.Content.ReadAsStringAsync());
        var result = callDoc.RootElement.GetProperty("result");
        Assert.False(result.GetProperty("isError").GetBoolean());
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("off", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rotating_the_token_invalidates_the_old_one_on_the_next_request()
    {
        var before = await _client.SendAsync(Request(Initialize(1)));
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        const string newToken = "rotated-token-fedcba9876543210";
        _store.Update(s => s.AiIntegration.Token = newToken);

        var withOldToken = await _client.SendAsync(Request(Initialize(2), bearerToken: Token));
        Assert.Equal(HttpStatusCode.Unauthorized, withOldToken.StatusCode);

        var withNewToken = await _client.SendAsync(Request(Initialize(3), bearerToken: newToken));
        Assert.Equal(HttpStatusCode.OK, withNewToken.StatusCode);
    }
}
