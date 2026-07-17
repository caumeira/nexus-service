using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors;
using Nexus.Service.Serialization;

namespace Nexus.Service.Mcp.Tools;

/// <summary>Read-only telemetry tool: a one-call CPU/GPU/memory/cooling snapshot.</summary>
public sealed class GetSystemOverviewTool : IMcpTool
{
    private readonly ISensorProvider _sensors;
    private readonly IFanControlProvider _fans;
    private readonly IConfigStore _store;

    public GetSystemOverviewTool(ISensorProvider sensors, IFanControlProvider fans, IConfigStore store)
    {
        _sensors = sensors;
        _fans = fans;
        _store = store;
    }

    public string Name => "get_system_overview";
    public string Title => "System Overview";

    public string Description =>
        "Returns a one-call snapshot of CPU, GPU, and memory load plus the active cooling preset. " +
        "Call this first, before get_sensors, get_cooling_state, or get_lighting_state, to decide " +
        "which of those more detailed tools is worth calling next.";

    public McpCapability Capability => McpCapability.Telemetry;
    public bool ReadOnly => true;
    public string InputSchemaJson => "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        var result = new McpSystemOverviewResult
        {
            CpuModel = _sensors.GetCpuModel(),
            GpuModels = new List<string>(_sensors.GetGpuModels()),
            MemoryTotal = _sensors.GetMemoryTotalFormatted(),
            Summary = SummarySensors.Build(_sensors),
            FanChannels = new List<FanChannel>(_fans.GetFanChannels()),
            ActiveCoolingPreset = _store.Load().Cooling.ActivePreset,
        };
        var json = JsonSerializer.Serialize(result, AppJsonContext.Default.McpSystemOverviewResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }
}
