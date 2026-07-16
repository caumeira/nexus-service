using System.Collections.Generic;

namespace Nexus.Service.Monitoring.History;

/// <summary>One GPU's load/temperature reading within a MetricSample.
/// GpuId is already MetricsHistory.SanitizeId-ed. AdapterLuid is the raw
/// GpuReadout.AdapterLuid captured at sample time ("" when unmatched or off
/// Windows); GET /monitoring/history resolves the wire adapterLuid live
/// instead of trusting a historical sample's value, so this field exists for
/// completeness rather than being read back by the route.</summary>
public sealed record GpuReading(
    string GpuId, string Name, string AdapterLuid, double? LoadPercent, double? TempC);

/// <summary>One fan channel's reading within a MetricSample. FanId is
/// already MetricsHistory.SanitizeId-ed. Rpm is null when the channel can't
/// report it (FanChannel.RpmUnavailable); Duty is always known since a
/// channel only exists in this list because it reported a commanded duty.</summary>
public sealed record FanReading(string FanId, string Name, int? Rpm, int? Duty);

/// <summary>
/// One second's worth of system metrics: wall-clock epoch-seconds timestamp
/// plus nullable scalars (null = that source failed this tick) and the
/// per-GPU / per-fan reading lists. The unit MetricsSampleBuffer buffers,
/// IMetricsHistoryStore persists, and GET /monitoring/history serves.
/// </summary>
public sealed record MetricSample(
    long TsSec,
    double? CpuPercent,
    double? MemoryPercent,
    double? NetInBytesPerSec,
    double? NetOutBytesPerSec,
    double? CpuTempC,
    IReadOnlyList<GpuReading> Gpus,
    IReadOnlyList<FanReading> Fans);
