using System;
using System.Collections.Generic;

namespace Nexus.Service.Diagnostics.Temperature;

/// <summary>One bucket for one component. BucketUtcMs is the bucket's start,
/// aligned to TemperatureSampler.BucketMinutes; AvgC/MaxC/Samples are derived
/// from every reading TemperatureSampler took at its tick cadence during
/// that bucket.</summary>
public sealed record TemperatureBucketRow(
    string ComponentId, string Kind, string Name, long BucketUtcMs,
    double AvgC, double MaxC, int Samples);

/// <summary>
/// Persistent store for temperature history buckets. TemperatureSampler is the
/// only writer (one flush per component per completed bucket); GET
/// /diagnostics/temperatures and the DiagnosticsHealthModel sustained-high
/// check are the readers.
/// </summary>
public interface ITemperatureHistoryStore : IDisposable
{
    /// <summary>Insert or replace rows keyed by (component_id, bucket_utc).</summary>
    void UpsertBuckets(IReadOnlyList<TemperatureBucketRow> rows);

    /// <summary>Rows with bucket_utc in [fromUtcMs, toUtcMs], ascending by bucket_utc.</summary>
    IReadOnlyList<TemperatureBucketRow> Query(long fromUtcMs, long toUtcMs);

    /// <summary>Deletes rows older than utcMs. Returns the number of rows deleted.</summary>
    int PruneOlderThan(long utcMs);
}
