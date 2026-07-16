using System;
using System.Collections.Generic;

namespace Nexus.Service.Monitoring.History;

/// <summary>
/// Persistent store for 1Hz system metrics. MetricsSampler is the only
/// writer (one Append per flush, every MetricsHistory.FlushSeconds ticks);
/// GET /monitoring/history is the reader, merging Query results against
/// MetricsSampleBuffer's unflushed tail.
/// </summary>
public interface IMetricsHistoryStore : IDisposable
{
    /// <summary>Batches samples in one transaction (INSERT OR REPLACE keyed
    /// by ts for scalars, by (ts, entity) for gpu/fan readings). When
    /// pruneCutoffSec is not null, also deletes every row older than it,
    /// inside that same transaction.</summary>
    void Append(IReadOnlyList<MetricSample> samples, long? pruneCutoffSec);

    /// <summary>Reconstructs samples with ts in [fromSec, toSec], ascending
    /// by ts.</summary>
    IReadOnlyList<MetricSample> Query(long fromSec, long toSec);
}
