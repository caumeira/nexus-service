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
    /// <summary>True if any process instance under this name owns a visible
    /// top-level window - Task-Manager-style App vs Background split.</summary>
    public bool IsApp { get; set; }
    /// <summary>Company/publisher name for the exe backing this process
    /// name, resolved lazily and cached server-side. Null until resolved or
    /// when unresolvable.</summary>
    public string? Publisher { get; set; }
    /// <summary>"signed", "unsigned", or "unknown" (platform can't tell).
    /// Null until the lazy resolve completes. See ProcessSignatureChecker.</summary>
    public string? Signed { get; set; }
    /// <summary>Combined disk read+write rate, bytes/sec, for this process
    /// instance. Zero on the process's first observed tick or where the
    /// platform call fails. Consumers aggregating by name sum it.</summary>
    public double StorageBytesPerSec { get; set; }
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
