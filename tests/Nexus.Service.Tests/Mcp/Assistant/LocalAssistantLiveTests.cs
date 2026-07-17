using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Mcp;
using Nexus.Service.Mcp.Assistant;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Sockets;
using Nexus.Service.Tests.Mcp;
using Xunit;

namespace Nexus.Service.Tests.Mcp.Assistant;

/// <summary>Live end-to-end check of the agentic loop against a real Ollama
/// runtime and a real model. Excluded from the default suite (Manual) because
/// it needs a running Ollama on the default port with a tool-capable model.
/// Run with an Ollama serving qwen3.5:0.8b on 127.0.0.1:11434:
///   dotnet test --filter "FullyQualifiedName~LocalAssistantLive"
/// The model tag is overridable via OLLAMA_LIVE_MODEL.</summary>
[Trait("Category", "Manual")]
public class LocalAssistantLiveTests
{
    [Fact]
    public async Task Real_model_drives_a_real_mcp_tool_and_answers()
    {
        var model = Environment.GetEnvironmentVariable("OLLAMA_LIVE_MODEL") ?? "qwen3.5:0.8b";
        var tempDir = Path.Combine(Path.GetTempPath(), "nexus-assistant-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var store = new TestableConfigStore(Path.Combine(tempDir, "settings.json"));
        store.Update(s =>
        {
            s.AiIntegration.Enabled = true;
            s.AiIntegration.AssistantActiveModel = model;
        });

        var hub = new MultiplexHub();
        var runtime = new OllamaRuntimeManager(
            http, new SystemOllamaProcessHost(), port => new OllamaClient(http, port),
            store, hub, tempDir, testHost: false);

        // Adopt the already-running system Ollama on the default port.
        await runtime.RefreshAsync(CancellationToken.None);
        Assert.True(runtime.State == AssistantRuntimeState.Running,
            $"No running Ollama detected on the default port (state={runtime.State}). Start one and pull {model}.");

        var tools = McpTestHarness.BuildRealTools(store, out var sensors, out _);
        sensors.Cpu = new List<HardwareSensor>
        {
            new() { Id = "cpu/temp", Name = "CPU Package", Type = "Temperature", Value = 58f, Units = "C" },
        };
        var registry = new McpToolRegistry(tools, store, new LoggingMcpAuditSink());
        var assistant = new LocalAssistant(registry, store, runtime);

        var outcome = await assistant.RunAsync(
            "What is my CPU temperature right now? Use a tool to find out.", CancellationToken.None);

        Assert.False(outcome.IsRefused, outcome.RefusalReason);
        Assert.NotNull(outcome.Response);
        Assert.NotEmpty(outcome.Response!.ToolsRun);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Response.Answer));
    }
}
