using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Monitoring.History.Binary;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History.Binary;

/// <summary>
/// AppUsageStore mechanism-level tests that AppUsageHistorySpec's
/// store-agnostic behavior specs cannot express: day-segment rollover
/// (ticks on different UTC days land in different segment files),
/// whole-day-file retention (a day strictly before the prune cutoff is
/// deleted from disk, not just hidden), the boundary day (astride the
/// cutoff) staying on disk while its pre-cutoff ticks are hidden at read
/// time, a torn trailing tick record (a crash mid-append) getting truncated
/// away before a later append so the new record stays reachable, and a
/// reader never observing a torn value while a writer is concurrently
/// appending.
/// </summary>
public class AppUsageStoreTests : IDisposable
{
    private readonly string _dir;

    public AppUsageStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-appusagestore-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static AppUsageTick CpuTick(long ts, params (string Name, double Value)[] apps) =>
        new(ts, new[] { new AppMetricSample("cpu", apps.Select(a => new AppUsagePoint(a.Name, a.Value, null)).ToList()) });

    [Fact]
    public void Append_TicksOnDifferentUtcDays_WriteToSeparateDaySegmentFiles()
    {
        using var store = new AppUsageStore(_dir, _ => null);

        store.Append(new[] { CpuTick(0, ("app.exe", 10)) }, null); // day 0
        store.Append(new[] { CpuTick(86_400, ("app.exe", 20)) }, null); // day 1

        Assert.True(File.Exists(Path.Combine(_dir, "cpu", "0.seg")));
        Assert.True(File.Exists(Path.Combine(_dir, "cpu", "1.seg")));

        var points = store.QueryAppSeries("cpu", "app.exe", 0, 200_000);
        Assert.Equal(new long[] { 0, 86_400 }, points.Select(p => p.TsSec).ToArray());
    }

    [Fact]
    public void Append_WithPruneCutoff_DeletesTheWholeDayFile_StrictlyBeforeTheCutoffDay()
    {
        using var store = new AppUsageStore(_dir, _ => null);
        store.Append(new[] { CpuTick(0, ("app.exe", 10)) }, null); // day 0

        store.Append(Array.Empty<AppUsageTick>(), pruneCutoffSec: 86_400); // cutoff day = 1

        Assert.False(File.Exists(Path.Combine(_dir, "cpu", "0.seg")));
        Assert.False(File.Exists(Path.Combine(_dir, "cpu", "0.ids")));
    }

    [Fact]
    public void Append_WithPruneCutoff_RetainsTheBoundaryDayFile_ButHidesPreCutoffTicksAtReadTime()
    {
        using var store = new AppUsageStore(_dir, _ => null);
        // Both ticks land in day 0; the cutoff below sits strictly between
        // them, so day 0 is NOT strictly before cutoff day (still 0) and
        // must stay on disk - only the read path hides the earlier tick.
        store.Append(new[] { CpuTick(1000, ("app.exe", 10)), CpuTick(5000, ("app.exe", 20)) }, null);

        store.Append(Array.Empty<AppUsageTick>(), pruneCutoffSec: 3000);

        Assert.True(File.Exists(Path.Combine(_dir, "cpu", "0.seg")));

        var points = store.QueryAppSeries("cpu", "app.exe", 0, 10_000);
        var point = Assert.Single(points);
        Assert.Equal(5000, point.TsSec);
    }

    [Fact]
    public void Append_WithPruneCutoff_ExactBoundary_ExcludesTheTickAtTheCutoffItself()
    {
        // SqliteMetricsHistoryStore.Prune deletes "ts < cutoff", keeping a
        // row whose ts equals the cutoff exactly - the read-time floor here
        // must draw the same line.
        using var store = new AppUsageStore(_dir, _ => null);
        store.Append(new[] { CpuTick(2999, ("app.exe", 1)), CpuTick(3000, ("app.exe", 2)) }, null);

        store.Append(Array.Empty<AppUsageTick>(), pruneCutoffSec: 3000);

        var points = store.QueryAppSeries("cpu", "app.exe", 0, 10_000);
        var point = Assert.Single(points);
        Assert.Equal(3000, point.TsSec);
    }

    [Fact]
    public void Append_WithATornTrailingTickRecord_TruncatesIt_SoALaterAppendLandsCleanly()
    {
        using var store = new AppUsageStore(_dir, _ => null);
        store.Append(new[] { CpuTick(1000, ("app.exe", 10)) }, null); // day 0

        // Simulate a crash mid-append: a trailing tick record whose declared
        // app count claims more entries than actually follow it.
        var segPath = Path.Combine(_dir, "cpu", "0.seg");
        using (var fs = new FileStream(segPath, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Seek(0, SeekOrigin.End);
            fs.Write(BitConverter.GetBytes(2000L));
            fs.Write(BitConverter.GetBytes((ushort)50)); // count: claims 50 app entries
            fs.Write(new byte[] { 1, 2, 3 });             // far short of that many entries
        }

        // A later append to the same day must land right after the last
        // good record, not behind the unreachable garbage - otherwise this
        // new tick would never be reachable again.
        store.Append(new[] { CpuTick(3000, ("app.exe", 30)) }, null);

        var points = store.QueryAppSeries("cpu", "app.exe", 0, 10_000);
        Assert.Equal(new long[] { 1000, 3000 }, points.Select(p => p.TsSec).ToArray());
    }

    [Fact]
    public void QueryFirstSeen_AfterPruneCutoffAdvancesPastTheOriginalTick_ReadsTheSurvivingLaterOne()
    {
        // The reappear-reads-fresh semantics the design calls for: no
        // orphan-eviction pass runs, the read-time floor alone makes a
        // pruned-out first sighting invisible.
        using var store = new AppUsageStore(_dir, _ => null);
        store.Append(new[] { CpuTick(1000, ("app.exe", 10)) }, null);
        store.Append(Array.Empty<AppUsageTick>(), pruneCutoffSec: 5000);

        store.Append(new[] { CpuTick(6000, ("app.exe", 20)) }, null);

        Assert.Equal(6000, store.QueryFirstSeen("app.exe"));
    }

    [Fact]
    public async Task ConcurrentQueries_DuringOngoingAppends_NeverThrow_AndNeverObserveATornValue()
    {
        using var store = new AppUsageStore(_dir, _ => null);
        var stop = false;
        Exception? readerException = null;

        var reader = Task.Run(() =>
        {
            try
            {
                while (!Volatile.Read(ref stop))
                {
                    foreach (var app in store.QueryTopApps("cpu", 0, long.MaxValue, 15))
                    {
                        // Every readable value came from a CRC-validated
                        // record; a torn write would either fail validation
                        // (record invisible) or, if this assertion ever
                        // fails, prove a torn value leaked through.
                        Assert.InRange(app.Avg, 0, 199);
                    }
                    foreach (var point in store.QueryAppSeries("cpu", "app.exe", 0, long.MaxValue))
                    {
                        Assert.InRange(point.Value ?? 0, 0, 199);
                    }
                }
            }
            catch (Exception ex)
            {
                readerException = ex;
            }
        });

        for (var i = 0; i < 200; i++)
        {
            store.Append(new[] { CpuTick(1000 + i, ("app.exe", i)) }, null);
        }

        Volatile.Write(ref stop, true);
        await reader;

        Assert.Null(readerException);
        var points = store.QueryAppSeries("cpu", "app.exe", 0, long.MaxValue);
        Assert.Equal(200, points.Count);
    }
}
