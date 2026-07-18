using System;
using System.Collections.Generic;

namespace Nexus.Service.Mcp.History;

/// <summary>
/// Fallback used when the binary history store can't be opened. Every write
/// is a no-op and every query returns empty; IsAvailable is false so the
/// history tools and <see cref="AiHistoryMcpAuditSink"/> can report
/// unavailability instead of silently returning empty data that reads as
/// "no history recorded yet".
/// </summary>
public sealed class UnavailableAiHistoryStore : IAiHistoryStore
{
    public bool IsAvailable => false;

    public void RecordSamples(IReadOnlyList<AiHistorySampleRow> rows, DateTime nowUtc) { }

    public IReadOnlyList<string> KnownSensorIds() => Array.Empty<string>();

    public AiHistorySeriesResult? QuerySensorHistory(string sensorId, long fromUtcMs, long toUtcMs, int maxPoints) => null;

    public IReadOnlyList<AiHistorySensorSummaryRow> Summarize(long fromUtcMs, long toUtcMs) => Array.Empty<AiHistorySensorSummaryRow>();

    public void RecordEvent(AiHistoryEventRow row) { }

    public AiHistoryEventQueryResult QueryEvents(long fromUtcMs, string? type, int limit) =>
        new(Array.Empty<AiHistoryEventRow>(), false);

    public void Dispose() { }
}
