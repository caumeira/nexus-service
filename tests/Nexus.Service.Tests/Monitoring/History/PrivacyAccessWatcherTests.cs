using System;
using System.Collections.Generic;
using Nexus.Service.Monitoring.History;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History;

/// <summary>
/// Drives PrivacyAccessWatcher through its internal Tick(DateTime) seam - the
/// BackgroundService loop itself (PeriodicTimer, the Windows platform gate)
/// is not under test here. The registry reader is stubbed via
/// IPrivacyAccessRegistryReader so no real console user or registry is
/// touched.
/// </summary>
public class PrivacyAccessWatcherTests
{
    private sealed class StubRegistryReader : IPrivacyAccessRegistryReader
    {
        public IReadOnlyList<PrivacyAccessRawEntry> NextSnapshot { get; set; } = Array.Empty<PrivacyAccessRawEntry>();

        public IReadOnlyList<PrivacyAccessRawEntry> ReadAll() => NextSnapshot;
    }

    private sealed class RecordingSessionStore : IPrivacySessionStore
    {
        public List<(string Capability, string AppId, long Start, long? End)> Upserts { get; } = new();
        public List<long> PruneCalls { get; } = new();

        public void Upsert(string capability, string appId, long startUtcSec, long? endUtcSec) =>
            Upserts.Add((capability, appId, startUtcSec, endUtcSec));

        public IReadOnlyList<PrivacySession> Query(long fromSec, long toSec) => Array.Empty<PrivacySession>();

        public void PruneOlderThan(long cutoffSec) => PruneCalls.Add(cutoffSec);
    }

    private const long StartFileTime = 116_444_736_100_000_000; // 10s past the Unix epoch

    [Fact]
    public void Tick_UpsertsAnOpenSession_WhenTheReaderReportsOneInUse()
    {
        var reader = new StubRegistryReader
        {
            NextSnapshot = new[] { new PrivacyAccessRawEntry("microphone", "app.exe", StartFileTime, 0) },
        };
        var store = new RecordingSessionStore();
        var watcher = new PrivacyAccessWatcher(reader, store);

        watcher.Tick(DateTime.UtcNow);

        var upsert = Assert.Single(store.Upserts);
        Assert.Equal("microphone", upsert.Capability);
        Assert.Equal("app.exe", upsert.AppId);
        Assert.Equal(10, upsert.Start);
        Assert.Null(upsert.End);
    }

    [Fact]
    public void Tick_ClosesADisappearedOpenSession_AtThisTicksTime()
    {
        var reader = new StubRegistryReader
        {
            NextSnapshot = new[] { new PrivacyAccessRawEntry("webcam", "app.exe", StartFileTime, 0) },
        };
        var store = new RecordingSessionStore();
        var watcher = new PrivacyAccessWatcher(reader, store);
        var firstTickUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        watcher.Tick(firstTickUtc);

        reader.NextSnapshot = Array.Empty<PrivacyAccessRawEntry>(); // user logged off
        var secondTickUtc = firstTickUtc.AddSeconds(30);
        watcher.Tick(secondTickUtc);

        Assert.Equal(2, store.Upserts.Count); // the open from the first tick, then the close
        var close = Assert.Single(store.Upserts, u => u.End is not null);
        Assert.Equal(new DateTimeOffset(secondTickUtc).ToUnixTimeSeconds(), close.End);
    }

    [Fact]
    public void Tick_DoesNotUpsertAgain_WhenNothingChangedBetweenPolls()
    {
        var reader = new StubRegistryReader
        {
            NextSnapshot = new[] { new PrivacyAccessRawEntry("microphone", "app.exe", StartFileTime, 0) },
        };
        var store = new RecordingSessionStore();
        var watcher = new PrivacyAccessWatcher(reader, store);
        watcher.Tick(DateTime.UtcNow);

        watcher.Tick(DateTime.UtcNow);

        Assert.Single(store.Upserts);
    }

    [Fact]
    public void Tick_Prunes_OnTheFirstTick()
    {
        // _lastPruneUtc starts at DateTime.MinValue, so the very first tick
        // is always "over an hour" since the last prune - mirrors
        // TemperatureRollup.MaybePrune enforcing retention immediately on a
        // fresh start rather than waiting a full hour after boot.
        var reader = new StubRegistryReader();
        var store = new RecordingSessionStore();
        var watcher = new PrivacyAccessWatcher(reader, store);

        watcher.Tick(DateTime.UtcNow);

        Assert.Single(store.PruneCalls);
    }

    [Fact]
    public void Tick_DoesNotPruneAgain_WithinAnHourOfTheFirstPrune()
    {
        var reader = new StubRegistryReader();
        var store = new RecordingSessionStore();
        var watcher = new PrivacyAccessWatcher(reader, store);
        var start = DateTime.UtcNow;

        watcher.Tick(start);
        watcher.Tick(start.AddMinutes(30));

        Assert.Single(store.PruneCalls);
    }

    [Fact]
    public void Tick_PrunesAgain_OnceAnHourElapsesSinceTheLastPrune()
    {
        var reader = new StubRegistryReader();
        var store = new RecordingSessionStore();
        var watcher = new PrivacyAccessWatcher(reader, store);
        var start = DateTime.UtcNow;

        watcher.Tick(start);
        watcher.Tick(start.AddHours(1));

        Assert.Equal(2, store.PruneCalls.Count);
    }

    [Fact]
    public void Tick_DoesNotThrow_WhenTheReaderThrows()
    {
        var reader = new ThrowingRegistryReader();
        var store = new RecordingSessionStore();
        var watcher = new PrivacyAccessWatcher(reader, store);

        var ex = Record.Exception(() => watcher.Tick(DateTime.UtcNow));

        Assert.NotNull(ex); // internal Tick propagates; the BackgroundService loop applies the warn throttle, not Tick itself.
        Assert.Empty(store.Upserts);
    }

    private sealed class ThrowingRegistryReader : IPrivacyAccessRegistryReader
    {
        public IReadOnlyList<PrivacyAccessRawEntry> ReadAll() => throw new InvalidOperationException("boom");
    }
}
