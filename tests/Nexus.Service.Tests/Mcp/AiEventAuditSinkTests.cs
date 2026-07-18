using System;
using Nexus.Service.Mcp;
using Nexus.Service.Mcp.History;
using Xunit;

namespace Nexus.Service.Tests.Mcp;

public class AiEventAuditSinkTests
{
    [Fact]
    public void Record_swallows_store_failures_so_callers_never_see_them()
    {
        var sink = new AiEventAuditSink(new ThrowingEventLog());

        var entry = new McpAuditEntry(
            "set_brightness", "{}", true, null, DateTimeOffset.UtcNow);

        // Auditing is best-effort by design: a runtime store failure must not
        // escape into the tool-call or /ai/config pipelines as a 500.
        var ex = Xunit.Record.Exception(() => sink.Record(entry));

        Assert.Null(ex);
    }

    private sealed class ThrowingEventLog : IAiEventLog
    {
        public bool IsAvailable => true;

        public void RecordEvent(AiHistoryEventRow row) =>
            throw new InvalidOperationException("disk full");

        public AiHistoryEventQueryResult QueryEvents(long fromUtcMs, string? type, int limit) =>
            new(Array.Empty<AiHistoryEventRow>(), false);
    }
}
