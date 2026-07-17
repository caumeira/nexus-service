using System.Collections.Generic;
using Nexus.Service.Models.Activity;

namespace Nexus.Service.Activity;

// WebSocket broadcast payloads for the "processes", "network", and "screentime"
// topics emitted by MonitoringBroadcaster.

public sealed class ProcessEntry
{
    public string Name { get; set; } = "";
    public double CpuPercent { get; set; }
    public double MemoryMb { get; set; }
    /// <summary>Process creation time, UTC epoch ms; null when unavailable.</summary>
    public long? StartedAtMs { get; set; }
}

public sealed class ProcessFrame
{
    public List<ProcessEntry> Processes { get; set; } = new();
    public double TotalCpu { get; set; }
    public double TotalMemoryPercent { get; set; }
}

public sealed class GpuProcessEntry
{
    public string Name { get; set; } = "";
    /// <summary>Summed GPU engine utilization across this adapter's engines, 0..100.</summary>
    public double GpuPercent { get; set; }
    /// <summary>Dedicated GPU memory in MB on this adapter.</summary>
    public double DedicatedMb { get; set; }
    /// <summary>Adapter LUID ("HighPart:LowPart") this row belongs to; "" if unknown.</summary>
    public string AdapterLuid { get; set; } = "";
}

public sealed class GpuProcessFrame
{
    public List<GpuProcessEntry> Processes { get; set; } = new();
}

public sealed class NetworkRateEntry
{
    public string Name { get; set; } = "";
    public double RateIn { get; set; }
    public double RateOut { get; set; }
}

public sealed class NetworkFrame
{
    public List<NetworkRateEntry> Entries { get; set; } = new();
}

public sealed class ScreenTimeFrame
{
    public FocusSession? Focus { get; set; }
    public List<AppUsage> History { get; set; } = new();
}
