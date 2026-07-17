using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Sensors;
using Nexus.Service.Serialization;

namespace Nexus.Service.Mcp.Tools;

/// <summary>Read-only telemetry tool: the full sensor list for one hardware device.</summary>
public sealed class GetSensorsTool : IMcpTool
{
    private readonly ISensorProvider _sensors;

    public GetSensorsTool(ISensorProvider sensors) => _sensors = sensors;

    public string Name => "get_sensors";
    public string Title => "Sensors";

    public string Description =>
        "Returns the full sensor list (current value, min, max, average, units) for one hardware " +
        "device: cpu, gpu, memory, motherboard, or storage. Call this after get_system_overview when " +
        "the summary numbers aren't enough - for example to read every CPU core clock, every GPU fan, " +
        "or a specific storage drive's SMART data (pass its id as 'drive').";

    public McpCapability Capability => McpCapability.Telemetry;
    public bool ReadOnly => true;

    public string InputSchemaJson =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"device\":{\"type\":\"string\",\"enum\":[\"cpu\",\"gpu\",\"memory\",\"motherboard\",\"storage\"]}," +
        "\"drive\":{\"type\":\"string\",\"description\":\"Storage drive id from a prior get_sensors(device=storage) call. Only used when device is storage.\"}" +
        "},\"required\":[\"device\"],\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        var device = McpArgs.StringArg(args, "device");
        if (string.IsNullOrEmpty(device))
        {
            return Task.FromResult(McpToolExecutionResult.Error(
                "'device' is required. Expected one of: cpu, gpu, memory, motherboard, storage."));
        }

        IReadOnlyList<HardwareSensor> sensors;
        switch (device)
        {
            case "cpu":
                sensors = _sensors.GetCpuSensors();
                break;
            case "gpu":
                sensors = _sensors.GetGpuSensors();
                break;
            case "memory":
                sensors = _sensors.GetMemorySensors();
                break;
            case "motherboard":
                sensors = _sensors.GetMotherboardSensors();
                break;
            case "storage":
                var storageResult = ResolveStorageSensors(args);
                if (storageResult.Error is not null)
                {
                    return Task.FromResult(McpToolExecutionResult.Error(storageResult.Error));
                }
                sensors = storageResult.Sensors!;
                break;
            default:
                return Task.FromResult(McpToolExecutionResult.Error(
                    $"Unknown device '{device}'. Expected one of: cpu, gpu, memory, motherboard, storage."));
        }

        var result = new McpSensorsResult { Device = device, Sensors = new List<HardwareSensor>(sensors) };
        var json = JsonSerializer.Serialize(result, AppJsonContext.Default.McpSensorsResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }

    private (List<HardwareSensor>? Sensors, string? Error) ResolveStorageSensors(JsonElement? args)
    {
        var components = _sensors.GetStorageComponents(includeSmart: true);
        var drive = McpArgs.StringArg(args, "drive");
        if (string.IsNullOrEmpty(drive))
        {
            var merged = new List<HardwareSensor>();
            foreach (var component in components.Values)
            {
                merged.AddRange(component.Sensors);
            }
            return (merged, null);
        }

        if (components.TryGetValue(drive, out var comp))
        {
            return (new List<HardwareSensor>(comp.Sensors), null);
        }

        var known = string.Join(", ", components.Keys);
        return (null, $"Unknown drive '{drive}'. Known drives: {(known.Length == 0 ? "(none detected)" : known)}");
    }
}
