using System;
using System.IO;
using Nexus.Service.Mcp.History;
using Nexus.Service.Mcp.History.Binary;
using Xunit;

namespace Nexus.Service.Tests.Mcp.History.Binary;

/// <summary>
/// BinaryAiEventLog behavior: round trip, newest-first ordering with a type
/// filter and truncation, survival across a reopen, and torn-trailing-record
/// recovery (a crash mid-append leaves a later event landing cleanly).
/// </summary>
public class BinaryAiEventLogTests : IDisposable
{
    private static readonly DateTime BaseUtc = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _dir;

    public BinaryAiEventLogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-binaryaieventlog-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static long ToMs(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();

    [Fact]
    public void QueryEvents_returns_newest_first_filters_by_type_and_reports_truncated()
    {
        var log = new BinaryAiEventLog(_dir);
        for (var i = 0; i < 5; i++)
        {
            var kind = i % 2 == 0 ? "ai_write" : "lifecycle";
            log.RecordEvent(new AiHistoryEventRow(ToMs(BaseUtc.AddSeconds(i)), kind, $"evt{i}", "{}", true, null));
        }

        var all = log.QueryEvents(0, null, limit: 10);
        Assert.False(all.Truncated);
        Assert.Equal(5, all.Events.Count);
        Assert.Equal("evt4", all.Events[0].Name);
        Assert.Equal("evt0", all.Events[^1].Name);

        var writesOnly = log.QueryEvents(0, "ai_write", limit: 10);
        Assert.Equal(3, writesOnly.Events.Count);
        Assert.All(writesOnly.Events, e => Assert.Equal("ai_write", e.Kind));

        var capped = log.QueryEvents(0, null, limit: 2);
        Assert.True(capped.Truncated);
        Assert.Equal(2, capped.Events.Count);
    }

    [Fact]
    public void QueryEvents_round_trips_failure_and_error_text()
    {
        var log = new BinaryAiEventLog(_dir);
        log.RecordEvent(new AiHistoryEventRow(ToMs(BaseUtc), "ai_write", "apply_cooling_preset", "{\"preset\":\"turbo\"}", false, "boom"));

        var result = log.QueryEvents(0, null, limit: 10);

        var e = Assert.Single(result.Events);
        Assert.False(e.Success);
        Assert.Equal("boom", e.ErrorText);
        Assert.Equal("{\"preset\":\"turbo\"}", e.ArgsJson);
    }

    [Fact]
    public void Events_SurviveReopen()
    {
        var log = new BinaryAiEventLog(_dir);
        log.RecordEvent(new AiHistoryEventRow(ToMs(BaseUtc), "ai_write", "apply_cooling_preset", "{}", true, null));

        var reopened = new BinaryAiEventLog(_dir);

        var events = reopened.QueryEvents(0, null, limit: 10);
        var e = Assert.Single(events.Events);
        Assert.Equal("apply_cooling_preset", e.Name);
    }

    [Fact]
    public void RecordEvent_WithATornTrailingRecord_TruncatesIt_SoALaterEventLandsCleanly()
    {
        var log = new BinaryAiEventLog(_dir);
        log.RecordEvent(new AiHistoryEventRow(ToMs(BaseUtc), "ai_write", "evt0", "{}", true, null));

        // Simulate a crash mid-append: a trailing record whose declared kind
        // length claims more bytes than actually follow it.
        var eventsPath = Path.Combine(_dir, "events.log");
        const int claimedKindLen = 500;
        using (var fs = new FileStream(eventsPath, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Seek(0, SeekOrigin.End);
            fs.Write(BitConverter.GetBytes(ToMs(BaseUtc.AddSeconds(1))));
            fs.Write(BitConverter.GetBytes(claimedKindLen));
            fs.Write(new byte[] { 1, 2, 3 }); // far short of the claimed length
        }

        var reopened = new BinaryAiEventLog(_dir);
        reopened.RecordEvent(new AiHistoryEventRow(ToMs(BaseUtc.AddSeconds(2)), "ai_write", "evt1", "{}", true, null));

        var events = reopened.QueryEvents(0, null, limit: 10);
        Assert.Equal(2, events.Events.Count);
        Assert.Equal("evt1", events.Events[0].Name);
        Assert.Equal("evt0", events.Events[1].Name);
    }
}
