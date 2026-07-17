using System;
using System.Collections.Generic;
using Nexus.Service.Mcp;
using Nexus.Service.Mcp.History;
using Xunit;

namespace Nexus.Service.Tests.Mcp;

public class SqliteMcpAuditSinkTests
{
    [Fact]
    public void Record_swallows_store_failures_so_callers_never_see_them()
    {
        var sink = new SqliteMcpAuditSink(new ThrowingHistoryStore());

        var entry = new McpAuditEntry(
            "set_brightness", "{}", true, null, DateTimeOffset.UtcNow);

        // Auditing is best-effort by design: a runtime store failure must not
        // escape into the tool-call or /ai/config pipelines as a 500.
        var ex = Xunit.Record.Exception(() => sink.Record(entry));

        Assert.Null(ex);
    }

    private sealed class ThrowingHistoryStore : IAiHistoryStore
    {
        public bool IsAvailable => true;

        public void RecordSamples(IReadOnlyList<AiHistorySampleRow> rows, DateTime nowUtc) =>
            throw new InvalidOperationException("disk full");

        public IReadOnlyList<string> KnownSensorIds() => Array.Empty<string>();

        public AiHistorySeriesResult? QuerySensorHistory(string sensorId, long fromUtcMs, long toUtcMs, int maxPoints) => null;

        public IReadOnlyList<AiHistorySensorSummaryRow> Summarize(long fromUtcMs, long toUtcMs) =>
            Array.Empty<AiHistorySensorSummaryRow>();

        public void RecordEvent(AiHistoryEventRow row) =>
            throw new InvalidOperationException("disk full");

        public AiHistoryEventQueryResult QueryEvents(long fromUtcMs, string? type, int limit) =>
            new(Array.Empty<AiHistoryEventRow>(), false);

        public void Dispose()
        {
        }
    }
}
