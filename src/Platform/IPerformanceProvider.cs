namespace Qos.Service.Platform;

/// <summary>
/// Cross-platform abstraction for reading system-wide CPU/Memory/GPU usage.
/// Each OS gets its own implementation; resolved once at startup via PerformanceProviderFactory.
/// </summary>
public interface IPerformanceProvider
{
    /// <summary>Take a single instantaneous reading.</summary>
    Task<PerformanceSnapshot> SampleAsync(CancellationToken ct = default);
}
