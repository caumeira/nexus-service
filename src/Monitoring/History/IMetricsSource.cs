using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Monitoring.History;

/// <summary>Gathers one MetricSample. The only production implementation is
/// SystemMetricsSource; tests substitute a stub so MetricsSamplerTests can
/// drive the sampler's tick/flush/prune wiring without live hardware.</summary>
public interface IMetricsSource
{
    /// <summary>Reads every metric for one tick, stamping the sample with
    /// tsSec (the sampler's wall-clock time, not a source-owned clock).</summary>
    Task<MetricSample> SampleAsync(long tsSec, CancellationToken ct);
}
