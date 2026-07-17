using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Mcp.Tools;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests.Mcp;

/// <summary>Unit coverage for the lighting write tools against the shared StubLightingProvider.</summary>
public sealed class LightingWriteToolsTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "nexus-mcp-lighting-write-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private TestableConfigStore NewStore() => new(Path.Combine(_tempDir, "settings.json"));

    // ── apply_lighting_scenario ─────────────────────────────────────────────

    [Fact]
    public async Task ApplyLightingScenario_valid_effect_starts_it_and_persists()
    {
        var lighting = new McpTestHarness.StubLightingProvider();
        var tool = new ApplyLightingScenarioTool(lighting, new MultiplexHub());

        var args = JsonSerializer.SerializeToElement(new { scenario = "rainbow" });
        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.False(result.IsError);
        var call = Assert.Single(lighting.StartAnimateCalls);
        Assert.Equal("rainbow", call.Effect);
        Assert.True(call.Persist);
    }

    [Fact]
    public async Task ApplyLightingScenario_unknown_scenario_is_error_and_does_not_start_anything()
    {
        var lighting = new McpTestHarness.StubLightingProvider();
        var tool = new ApplyLightingScenarioTool(lighting, new MultiplexHub());

        var args = JsonSerializer.SerializeToElement(new { scenario = "not-a-real-effect" });
        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Empty(lighting.StartAnimateCalls);
    }

    // ── set_static_color ────────────────────────────────────────────────────

    [Fact]
    public async Task SetStaticColor_valid_hex_persists_and_drives_the_simple_shader()
    {
        var store = NewStore();
        var lighting = new McpTestHarness.StubLightingProvider();
        var tool = new SetStaticColorTool(lighting, store, new MultiplexHub());

        var args = JsonSerializer.SerializeToElement(new { color = "#ff0000" });
        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.False(result.IsError);
        var call = Assert.Single(lighting.StartAnimateCalls);
        Assert.StartsWith("simple", call.Effect, StringComparison.Ordinal);
        Assert.Equal(1f, call.Saturation, precision: 3);

        var color = store.Load().Lighting.StaticColor;
        Assert.Equal((byte)255, color.R);
        Assert.Equal((byte)0, color.G);
        Assert.Equal((byte)0, color.B);
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#ff00")]
    [InlineData("#gggggg")]
    [InlineData("ff0000")]
    public async Task SetStaticColor_invalid_hex_is_error(string color)
    {
        var tool = new SetStaticColorTool(new McpTestHarness.StubLightingProvider(), NewStore(), new MultiplexHub());

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { color }), CancellationToken.None);

        Assert.True(result.IsError);
    }

    // ── set_brightness ──────────────────────────────────────────────────────

    [Fact]
    public async Task SetBrightness_valid_percent_persists_the_clamped_fraction()
    {
        var store = NewStore();
        var tool = new SetBrightnessTool(store, new MultiplexHub());

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { percent = 60 }), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(0.6f, store.Load().Lighting.GlobalBrightness, precision: 3);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(150)]
    public async Task SetBrightness_out_of_range_percent_is_error(double percent)
    {
        var tool = new SetBrightnessTool(NewStore(), new MultiplexHub());

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { percent }), CancellationToken.None);

        Assert.True(result.IsError);
    }

    // ── stop_lighting ───────────────────────────────────────────────────────

    [Fact]
    public async Task StopLighting_calls_stop_all()
    {
        var lighting = new McpTestHarness.StubLightingProvider();
        var tool = new StopLightingTool(lighting, new MultiplexHub());

        var result = await tool.ExecuteAsync(null, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(1, lighting.StopAllCallCount);
    }
}
