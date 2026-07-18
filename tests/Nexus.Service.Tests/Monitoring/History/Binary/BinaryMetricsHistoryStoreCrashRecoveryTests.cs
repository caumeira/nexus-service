using System;
using System.IO;
using System.Linq;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Monitoring.History.Binary;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History.Binary;

/// <summary>
/// Facade-level crash-recovery tests for BinaryMetricsHistoryStore (Phase 6
/// of the metrics-store design): proves the four recovery properties the
/// design's "Crash-safety + concurrency" section calls for hold through the
/// full facade, not just the per-component units that already pin most of
/// them directly (SuperBlockTests' torn-header/both-corrupted cases,
/// RingFileTests' torn-stamp/Crc/body cases). What is new here: a single
/// reopen recovering scalar, app-usage, and privacy data together (a wiring
/// bug in the constructor - the wrong path handed to one subsystem - would
/// silently break only one of the three), the facade still opening after a
/// fully corrupted superblock and keeping data the self-validating rings
/// never depended on it for, and the app-usage day-segment torn-tail
/// recovery (AppUsageStore.TruncateTornTail) reached through
/// IAppUsageHistoryStore.Append rather than the store directly.
/// </summary>
public class BinaryMetricsHistoryStoreCrashRecoveryTests : IDisposable
{
    private readonly string _dir;

    public BinaryMetricsHistoryStoreCrashRecoveryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-binaryhistory-crash-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static MetricSample Scalars(long ts, double? cpu = 50) =>
        new(ts, cpu, 60, 1000, 500, 55, Array.Empty<GpuReading>(), Array.Empty<FanReading>());

    private static AppUsageTick CpuTick(long ts, params (string Name, double Value)[] apps) =>
        new(ts, new[] { new AppMetricSample("cpu", apps.Select(a => new AppUsagePoint(a.Name, a.Value, null)).ToList()) });

    [Fact]
    public void Reopen_RecoversScalarData_AppUsageData_AndPrivacySessions_Together()
    {
        using (var store = new BinaryMetricsHistoryStore(_dir))
        {
            store.Append(new[] { Scalars(1000, cpu: 42) }, null);
            store.Append(new[] { CpuTick(1000, ("app.exe", 30)) }, null);
            store.Upsert("microphone", "app.exe", 1000, null);
        }

        using var reopened = new BinaryMetricsHistoryStore(_dir);

        var scalarRow = Assert.Single(reopened.Query(0, 10_000));
        Assert.Equal(42, scalarRow.CpuPercent);

        var appRow = Assert.Single(reopened.QueryTopApps("cpu", 0, 10_000, 15));
        Assert.Equal("app.exe", appRow.Name);

        var privacyRow = Assert.Single(((IPrivacySessionStore)reopened).Query(0, 10_000));
        Assert.Equal("microphone", privacyRow.Capability);
        Assert.Null(privacyRow.EndUtcSec);
    }

    [Fact]
    public void Reopen_WithBothSuperblockSlotsCorrupted_StillOpens_AndKeepsPreviouslyWrittenScalarData()
    {
        using (var store = new BinaryMetricsHistoryStore(_dir))
        {
            store.Append(new[] { Scalars(1000, cpu: 42) }, null);
        }

        var superPath = Path.Combine(_dir, "super");
        using (var fs = new FileStream(superPath, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Write(new byte[8192]); // zero out both 4 KiB superblock slots
        }

        using var reopened = new BinaryMetricsHistoryStore(_dir);

        // The superblock resets to "no prune floor" (matches SuperBlockTests'
        // Open_WithBothSlotsCorrupted_BehavesLikeAFreshFile), but the scalar
        // ring's own slots are self-validating independent of the
        // superblock, so the previously written sample is not lost.
        var row = Assert.Single(reopened.Query(0, 10_000));
        Assert.Equal(42, row.CpuPercent);
    }

    [Fact]
    public void Reopen_WithATornAppUsageDaySegment_RecoversEarlierTicks_AndAcceptsNewOnesAfterReopen()
    {
        using (var store = new BinaryMetricsHistoryStore(_dir))
        {
            store.Append(new[] { CpuTick(1000, ("app.exe", 10)) }, null); // day 0
        }

        // Simulate a crash mid-append: a trailing tick record whose declared
        // app count claims more entries than actually follow it.
        var segPath = Path.Combine(_dir, "apps", "cpu", "0.seg");
        using (var fs = new FileStream(segPath, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Seek(0, SeekOrigin.End);
            fs.Write(BitConverter.GetBytes(2000L));
            fs.Write(BitConverter.GetBytes((ushort)50)); // count: claims 50 app entries
            fs.Write(new byte[] { 1, 2, 3 });             // far short of that many entries
        }

        using var reopened = new BinaryMetricsHistoryStore(_dir);
        reopened.Append(new[] { CpuTick(3000, ("app.exe", 30)) }, null);

        var points = reopened.QueryAppSeries("cpu", "app.exe", 0, 10_000);
        Assert.Equal(new long[] { 1000, 3000 }, points.Select(p => p.TsSec).ToArray());
    }
}
