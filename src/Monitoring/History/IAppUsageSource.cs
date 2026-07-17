using System.Collections.Generic;

namespace Nexus.Service.Monitoring.History;

/// <summary>Gathers one per-app usage sampling tick from already-live snapshot
/// state (ProcessMonitor / GpuProcessMonitor) - no I/O of its own, so this is
/// synchronous unlike IMetricsSource. The only production implementation is
/// ProcessAppUsageSource; tests substitute a stub.</summary>
public interface IAppUsageSource
{
    /// <summary>Every metric's top-N apps for the current instant. Empty
    /// when no process/gpu-process snapshot has been produced yet.</summary>
    IReadOnlyList<AppMetricSample> Sample();
}
