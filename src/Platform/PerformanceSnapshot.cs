namespace Nexus.Service.Platform;

/// <summary>
/// One sample of system-wide performance counters.
/// All percentages are 0..100. Null means the current platform/provider
/// has no way to read that metric (graceful degradation rather than fake zeros).
/// </summary>
public sealed record PerformanceSnapshot
{
    public double? Cpu { get; init; }
    public double? Memory { get; init; }
    public double? Gpu { get; init; }

    /// <summary>Provider identifier so the client can show "macOS top" / "LibreHardwareMonitor" / "stub".</summary>
    public string Source { get; init; } = "unknown";
}
