using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Lifecycle;
using Nexus.Service.Mcp;
using Nexus.Service.Mcp.Tools;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests.Mcp;

/// <summary>
/// Unit coverage for the cooling write tools against the same stub layering
/// McpTestHarness uses for the read tools, plus the real StubCoolingProvider
/// (the actual production ICurveProvider on every platform) so SetCurves runs
/// its real persistence logic instead of a test double.
/// </summary>
public sealed class CoolingWriteToolsTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "nexus-mcp-cooling-write-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private TestableConfigStore NewStore() => new(Path.Combine(_tempDir, "settings.json"));

    private static McpTestHarness.StubFanControlProvider NewFans() => new()
    {
        Channels = new List<FanChannel>
        {
            new() { Id = "fan-1", Name = "Fan 1" },
            new() { Id = "fan-2", Name = "Fan 2" },
            new() { Id = "fan-3", Name = "Fan 3" },
        },
        Sources = new List<TemperatureSource>
        {
            new() { Id = "cpu-package", Name = "CPU Package", Category = "CPU" },
            new() { Id = "gpu-core", Name = "GPU Core", Category = "GPU" },
        },
    };

    // ── apply_cooling_preset ────────────────────────────────────────────────

    [Fact]
    public async Task ApplyCoolingPreset_valid_preset_applies_and_updates_active_preset()
    {
        var store = NewStore();
        var fans = NewFans();
        var tool = new ApplyCoolingPresetTool(fans, store, new MultiplexHub());

        var args = JsonSerializer.SerializeToElement(new { preset = "balanced" });
        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("balanced", store.Load().Cooling.ActivePreset);
    }

    [Fact]
    public async Task ApplyCoolingPreset_unknown_preset_is_error_listing_valid_values()
    {
        var tool = new ApplyCoolingPresetTool(NewFans(), NewStore(), new MultiplexHub());

        var args = JsonSerializer.SerializeToElement(new { preset = "ludicrous" });
        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("off", result.Text, StringComparison.Ordinal);
        Assert.Contains("turbo", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyCoolingPreset_missing_preset_is_error()
    {
        var tool = new ApplyCoolingPresetTool(NewFans(), NewStore(), new MultiplexHub());

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { }), CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ApplyCoolingPreset_gate_off_is_error_and_does_not_apply()
    {
        var store = NewStore();
        store.Update(s => s.Features.Cooling = false);
        var before = store.Load().Cooling.ActivePreset;
        var tool = new ApplyCoolingPresetTool(NewFans(), store, new MultiplexHub(), new FeatureGates(store));

        var args = JsonSerializer.SerializeToElement(new { preset = "balanced" });
        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(before, store.Load().Cooling.ActivePreset);
    }

    // ── set_global_fan_speed ────────────────────────────────────────────────

    [Fact]
    public async Task SetGlobalFanSpeed_valid_percent_persists_modifier_and_preserves_curves()
    {
        var store = NewStore();
        store.Update(s => s.Cooling.Curves.Add(new CurveDocument
        {
            Id = "preset-silent",
            Name = "Silent",
            Type = "Linear",
            Preset = "silent",
            Outputs = new List<CurveOutputDocument> { new() { Id = "fan-1", Type = "Fan" } },
        }));
        var fans = NewFans();
        var tool = new SetGlobalFanSpeedTool(new StubCoolingProvider(store), fans, store, new MultiplexHub());

        var args = JsonSerializer.SerializeToElement(new { percent = 50 });
        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.False(result.IsError);
        var cooling = store.Load().Cooling;
        Assert.Equal(0.5, cooling.GlobalSpeedModifier, precision: 6);
        var preserved = Assert.Single(cooling.Curves);
        Assert.Equal("preset-silent", preserved.Id);
        Assert.Equal("silent", preserved.Preset);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task SetGlobalFanSpeed_out_of_range_percent_is_error(double percent)
    {
        var store = NewStore();
        var tool = new SetGlobalFanSpeedTool(new StubCoolingProvider(store), NewFans(), store, new MultiplexHub());

        var args = JsonSerializer.SerializeToElement(new { percent });
        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task SetGlobalFanSpeed_gate_off_is_error_and_does_not_persist()
    {
        var store = NewStore();
        store.Update(s => s.Features.Cooling = false);
        var before = store.Load().Cooling.GlobalSpeedModifier;
        var tool = new SetGlobalFanSpeedTool(new StubCoolingProvider(store), NewFans(), store, new MultiplexHub(), new FeatureGates(store));

        var args = JsonSerializer.SerializeToElement(new { percent = 50 });
        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(before, store.Load().Cooling.GlobalSpeedModifier, precision: 6);
    }

    // ── set_fan_curve ───────────────────────────────────────────────────────

    private static JsonElement CurveArgs(object[] points, string[] outputs, string? input = null, string? name = null) =>
        JsonSerializer.SerializeToElement(new { points, outputs, input, name });

    [Fact]
    public async Task SetFanCurve_happy_path_creates_a_graph_curve_on_the_given_outputs()
    {
        var store = NewStore();
        var fans = NewFans();
        var tool = new SetFanCurveTool(new StubCoolingProvider(store), fans, store, new MultiplexHub());

        var args = CurveArgs(
            new object[] { new { temp = 30, speed = 20 }, new { temp = 80, speed = 100 } },
            new[] { "fan-1", "fan-2" });
        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.False(result.IsError);
        var curve = Assert.Single(store.Load().Cooling.Curves);
        Assert.Equal("Graph", curve.Type);
        Assert.Equal("cpu-package", curve.Input.Id);
        Assert.Equal(new[] { "fan-1", "fan-2" }, curve.Outputs.Select(o => o.Id));
        Assert.NotNull(curve.Graph);
        Assert.Equal(2, curve.Graph!.Points.Count);
    }

    [Fact]
    public async Task SetFanCurve_detaches_the_requested_output_from_other_curves_only()
    {
        var store = NewStore();
        store.Update(s => s.Cooling.Curves.Add(new CurveDocument
        {
            Id = "curve-existing",
            Name = "Existing",
            Type = "Flat",
            Outputs = new List<CurveOutputDocument>
            {
                new() { Id = "fan-1", Type = "Fan" },
                new() { Id = "fan-3", Type = "Fan" },
            },
        }));
        var fans = NewFans();
        var tool = new SetFanCurveTool(new StubCoolingProvider(store), fans, store, new MultiplexHub());

        var args = CurveArgs(new object[] { new { temp = 40, speed = 50 } }, new[] { "fan-1" });
        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.False(result.IsError);
        var curves = store.Load().Cooling.Curves;
        var existing = curves.Single(c => c.Id == "curve-existing");
        Assert.DoesNotContain(existing.Outputs, o => o.Id == "fan-1");
        Assert.Contains(existing.Outputs, o => o.Id == "fan-3");
        var newCurve = curves.Single(c => c.Id != "curve-existing");
        Assert.Contains(newCurve.Outputs, o => o.Id == "fan-1");
    }

    [Fact]
    public async Task SetFanCurve_repeat_call_with_same_name_updates_in_place_instead_of_duplicating()
    {
        var store = NewStore();
        var fans = NewFans();
        var tool = new SetFanCurveTool(new StubCoolingProvider(store), fans, store, new MultiplexHub());

        await tool.ExecuteAsync(CurveArgs(new object[] { new { temp = 30, speed = 20 } }, new[] { "fan-1" }, name: "My Curve"), CancellationToken.None);
        await tool.ExecuteAsync(CurveArgs(new object[] { new { temp = 40, speed = 60 } }, new[] { "fan-1" }, name: "My Curve"), CancellationToken.None);

        var curve = Assert.Single(store.Load().Cooling.Curves);
        Assert.Equal(60, curve.Graph!.Points.Single().Speed);
    }

    [Fact]
    public async Task SetFanCurve_empty_points_is_error()
    {
        var store = NewStore();
        var tool = new SetFanCurveTool(new StubCoolingProvider(store), NewFans(), store, new MultiplexHub());

        var result = await tool.ExecuteAsync(CurveArgs(Array.Empty<object>(), new[] { "fan-1" }), CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task SetFanCurve_empty_outputs_is_error()
    {
        var store = NewStore();
        var tool = new SetFanCurveTool(new StubCoolingProvider(store), NewFans(), store, new MultiplexHub());

        var result = await tool.ExecuteAsync(
            CurveArgs(new object[] { new { temp = 30, speed = 20 } }, Array.Empty<string>()), CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task SetFanCurve_unknown_output_id_is_error_listing_known_ids()
    {
        var store = NewStore();
        var tool = new SetFanCurveTool(new StubCoolingProvider(store), NewFans(), store, new MultiplexHub());

        var result = await tool.ExecuteAsync(
            CurveArgs(new object[] { new { temp = 30, speed = 20 } }, new[] { "not-a-fan" }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("fan-1", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetFanCurve_point_speed_out_of_range_is_error()
    {
        var store = NewStore();
        var tool = new SetFanCurveTool(new StubCoolingProvider(store), NewFans(), store, new MultiplexHub());

        var result = await tool.ExecuteAsync(
            CurveArgs(new object[] { new { temp = 30, speed = 250 } }, new[] { "fan-1" }), CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task SetFanCurve_invalid_input_category_is_error()
    {
        var store = NewStore();
        var tool = new SetFanCurveTool(new StubCoolingProvider(store), NewFans(), store, new MultiplexHub());

        var result = await tool.ExecuteAsync(
            CurveArgs(new object[] { new { temp = 30, speed = 20 } }, new[] { "fan-1" }, input: "ambient"), CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task SetFanCurve_gate_off_is_error_and_does_not_create_a_curve()
    {
        var store = NewStore();
        store.Update(s => s.Features.Cooling = false);
        var tool = new SetFanCurveTool(new StubCoolingProvider(store), NewFans(), store, new MultiplexHub(), new FeatureGates(store));

        var result = await tool.ExecuteAsync(
            CurveArgs(new object[] { new { temp = 30, speed = 20 } }, new[] { "fan-1" }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Empty(store.Load().Cooling.Curves);
    }

    [Fact]
    public async Task SetFanCurve_no_matching_temperature_source_is_error()
    {
        var store = NewStore();
        var fans = new McpTestHarness.StubFanControlProvider
        {
            Channels = new List<FanChannel> { new() { Id = "fan-1", Name = "Fan 1" } },
            Sources = new List<TemperatureSource>(),
        };
        var tool = new SetFanCurveTool(new StubCoolingProvider(store), fans, store, new MultiplexHub());

        var result = await tool.ExecuteAsync(
            CurveArgs(new object[] { new { temp = 30, speed = 20 } }, new[] { "fan-1" }), CancellationToken.None);

        Assert.True(result.IsError);
    }

    // ── audit ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Registry_records_a_cooling_write_call_in_the_audit_sink_with_args()
    {
        var store = NewStore();
        var tool = new ApplyCoolingPresetTool(NewFans(), store, new MultiplexHub());
        var audit = new RecordingAuditSink();
        var registry = new McpToolRegistry(new IMcpTool[] { tool }, store, audit);

        var args = JsonSerializer.SerializeToElement(new { preset = "turbo" });
        await registry.CallAsync(tool, args, CancellationToken.None);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal("apply_cooling_preset", entry.ToolName);
        Assert.True(entry.Success);
        Assert.Contains("turbo", entry.ArgsJson, StringComparison.Ordinal);
    }

    private sealed class RecordingAuditSink : IMcpAuditSink
    {
        public List<McpAuditEntry> Entries { get; } = new();
        public void Record(McpAuditEntry entry) => Entries.Add(entry);
    }
}
