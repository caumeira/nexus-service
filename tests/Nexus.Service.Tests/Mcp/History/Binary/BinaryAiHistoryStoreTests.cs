using System;
using System.IO;
using Nexus.Service.Mcp.History;
using Nexus.Service.Mcp.History.Binary;
using Xunit;

namespace Nexus.Service.Tests.Mcp.History.Binary;

/// <summary>
/// BinaryAiHistoryStore mechanism-level tests that AiHistoryStoreSpec's
/// store-agnostic behavior specs cannot express: sensor GlobalId stability
/// across a reopen with more than one sensor registered, the events log's
/// torn-trailing-record recovery (a crash mid-append), and a corrupted
/// sensor-meta snapshot falling back to empty rather than crashing the
/// store open.
/// </summary>
public class BinaryAiHistoryStoreTests : IDisposable
{
    private static readonly DateTime BaseUtc = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _dir;

    public BinaryAiHistoryStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-binaryaihistory-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static long ToMs(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();

    [Fact]
    public void Reopen_KeepsEverySensorsGlobalId_Stable_WithMultipleSensorsRegistered()
    {
        using (var store = new BinaryAiHistoryStore(_dir))
        {
            store.RecordSamples(new[]
            {
                new AiHistorySampleRow("cpu-temp", "CPU Temperature", "temperature", "C", 40, ToMs(BaseUtc)),
                new AiHistorySampleRow("gpu-temp", "GPU Temperature", "temperature", "C", 50, ToMs(BaseUtc)),
                new AiHistorySampleRow("fan-0", "Fan 1", "fan", "RPM", 1200, ToMs(BaseUtc)),
            }, BaseUtc);
        }

        using var reopened = new BinaryAiHistoryStore(_dir);
        reopened.RecordSamples(new[]
        {
            new AiHistorySampleRow("gpu-temp", "GPU Temperature", "temperature", "C", 55, ToMs(BaseUtc.AddSeconds(5))),
        }, BaseUtc.AddSeconds(5));

        var series = reopened.QuerySensorHistory("gpu-temp", ToMs(BaseUtc) - 1000, ToMs(BaseUtc) + 10_000, maxPoints: 10);
        Assert.NotNull(series);
        Assert.Equal(2, series!.Points.Count);
        Assert.Equal(new[] { "cpu-temp", "fan-0", "gpu-temp" }, reopened.KnownSensorIds());
    }

    [Fact]
    public void RecordEvent_WithATornTrailingRecord_TruncatesIt_SoALaterEventLandsCleanly()
    {
        using var store = new BinaryAiHistoryStore(_dir);
        store.RecordEvent(new AiHistoryEventRow(ToMs(BaseUtc), "ai_write", "evt0", "{}", true, null));

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

        var reopened = new BinaryAiHistoryStore(_dir);
        reopened.RecordEvent(new AiHistoryEventRow(ToMs(BaseUtc.AddSeconds(2)), "ai_write", "evt1", "{}", true, null));

        var events = reopened.QueryEvents(0, null, limit: 10);
        Assert.Equal(2, events.Events.Count);
        Assert.Equal("evt1", events.Events[0].Name);
        Assert.Equal("evt0", events.Events[1].Name);
    }

    [Fact]
    public void Constructor_WithACorruptedSensorMetaFile_FallsBackToEmpty_WithoutThrowing()
    {
        using (var store = new BinaryAiHistoryStore(_dir))
        {
            store.RecordSamples(new[] { new AiHistorySampleRow("cpu-temp", "CPU Temperature", "temperature", "C", 40, ToMs(BaseUtc)) }, BaseUtc);
        }

        File.WriteAllBytes(Path.Combine(_dir, "sensors.meta"), new byte[] { 1, 2, 3, 4, 5 });

        using var reopened = new BinaryAiHistoryStore(_dir);

        Assert.Empty(reopened.KnownSensorIds());
        Assert.Null(reopened.QuerySensorHistory("cpu-temp", 0, ToMs(BaseUtc) + 1000, maxPoints: 10));
    }
}
