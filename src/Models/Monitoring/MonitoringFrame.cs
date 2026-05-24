using System.Collections.Generic;
using Nexus.Service.Activity;
using Nexus.Service.Models.Sensors;

namespace Nexus.Service.Models.Monitoring;

/// <summary>
/// Composite payload for the "monitoring" WebSocket topic. Contains all sensor,
/// process, and network data in a single message so the overview UI renders
/// every card in one React commit.
/// </summary>
public sealed class MonitoringFrame
{
    public HardwareComponent? Cpu { get; set; }
    public List<HardwareComponent>? Gpu { get; set; }
    public HardwareComponent? Memory { get; set; }
    public Dictionary<string, StorageComponent>? Storage { get; set; }
    public HardwareComponent? Motherboard { get; set; }

    public string CpuModel { get; set; } = "";
    public List<string> GpuModels { get; set; } = new();
    public string MemoryTotal { get; set; } = "";
    public string MotherboardModel { get; set; } = "";

    public ProcessFrame? Processes { get; set; }
    public NetworkFrame? Network { get; set; }
}
