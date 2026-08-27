using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Monitoring.History.Binary;
using Xunit;
using Xunit.Abstractions;

namespace Nexus.Service.Tests.Monitoring.History;

/// <summary>
/// Measures the real on-disk cpu+memory footprint at
/// MetricsHistory.TopAppsPerSample against a fully-saturated tick (every
/// slot filled, the worst case for storage - almost every live process has a
/// nonzero reading, so the epsilon filter alone rarely shrinks that table),
/// then projects it to a full MetricsHistory.RetentionDays window. This is
/// the evidence behind the cap chosen on TopAppsPerSample's doc comment.
/// Runs against BinaryMetricsHistoryStore (BinaryAppUsageStorageEstimateTests,
/// the day-segment format), measuring the reduction its per-day 16-bit local
/// app id encoding buys.
///
/// The gpu table is not included in this cpu+memory projection: concurrent
/// GPU-active processes are bounded by how many actually use the GPU at
/// once, which in practice stays far below this cap regardless of adapter
/// count, unlike cpu/memory where a saturated tick is the realistic case.
/// </summary>
public abstract class AppUsageStorageEstimateSpec : IDisposable
{
    protected readonly string Dir;
    protected readonly IAppUsageHistoryStore Store;
    private readonly ITestOutputHelper _output;

    protected AppUsageStorageEstimateSpec(ITestOutputHelper output)
    {
        _output = output;
        Dir = Path.Combine(Path.GetTempPath(), "nexus-appusagestorage-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Dir);
        Store = CreateStore(Dir);
    }

    protected abstract IAppUsageHistoryStore CreateStore(string dir);

    // Both concrete stores implement IMetricsHistoryStore too (the same
    // object Store points at) - the vram test below needs the scalar side to
    // register a gpu_series/GpuRingStore identity first, the same dependency
    // AppUsageHistorySpec's gpu:<gid>/vram:<gid> tests have.
    private void AppendScalar(MetricSample sample) => ((IMetricsHistoryStore)Store).Append(new[] { sample }, null);

    /// <summary>Total bytes this store has written to disk so far - the
    /// recursive sum of the app-data directory.</summary>
    protected abstract long MeasureStorageBytes();

    /// <summary>Ceiling for the projected 7-day cpu+memory footprint - a
    /// generous bound comfortably above the measured projection at the
    /// current cap, so this fails loudly if TopAppsPerSample is ever raised
    /// again without re-checking the storage cost, rather than on ordinary
    /// measurement noise.</summary>
    protected abstract long CpuMemBudgetBytes { get; }

    /// <summary>Ceiling for the projected 7-day worst-case vram footprint
    /// (every tick saturates the cap for one gpu) - see CpuMemBudgetBytes for
    /// the same "generous, not tight" intent.</summary>
    protected abstract long VramWorstCaseBudgetBytes { get; }

    /// <summary>Ceiling for the projected 7-day app_storage_seconds footprint
    /// - same intent as CpuMemBudgetBytes, measured separately since storage
    /// is its own narrow table (value_bps, not value_x10) with no gpu
    /// dimension.</summary>
    protected abstract long StorageBudgetBytes { get; }

    public virtual void Dispose()
    {
        (Store as IDisposable)?.Dispose();
        try { Directory.Delete(Dir, recursive: true); } catch { }
    }

    [Fact]
    public void SevenDayProjection_AtTopAppsPerSampleCap_StaysUnderTheStorageBudget()
    {
        const int simulatedTicks = 500;
        const int distinctApps = 100; // > TopAppsPerSample, so every tick saturates the cap.
        var random = new Random(1);
        // Appended as one batch: same rows and same on-disk bytes as one call
        // per tick, without paying a segment write per tick in a test that only
        // measures size.
        var ticks = new List<AppUsageTick>(simulatedTicks);

        for (var t = 0; t < simulatedTicks; t++)
        {
            var ts = 1_000_000 + t * (long)MetricsHistory.AppSampleIntervalSeconds;
            var cpuApps = Enumerable.Range(0, distinctApps)
                .OrderBy(_ => random.Next())
                .Take(MetricsHistory.TopAppsPerSample)
                .Select(i => new AppUsagePoint($"app{i}.exe", random.Next(1, 1000) / 10.0, null))
                .ToList();
            var memApps = Enumerable.Range(0, distinctApps)
                .OrderBy(_ => random.Next())
                .Take(MetricsHistory.TopAppsPerSample)
                .Select(i => new AppUsagePoint($"app{i}.exe", random.Next(1, 500_000) / 10.0, null))
                .ToList();

            ticks.Add(new AppUsageTick(ts, new[]
            {
                new AppMetricSample("cpu", cpuApps),
                new AppMetricSample("memory", memApps),
            }));
        }

        Store.Append(ticks, null);

        var bytesForSimulatedWindow = MeasureStorageBytes();
        var rowsWritten = (long)simulatedTicks * MetricsHistory.TopAppsPerSample * 2; // cpu + memory
        var bytesPerRow = bytesForSimulatedWindow / (double)rowsWritten;

        var ticksPerDay = 86_400 / MetricsHistory.AppSampleIntervalSeconds;
        var projectedRows = (long)ticksPerDay * MetricsHistory.TopAppsPerSample * 2 * MetricsHistory.RetentionDays;
        var projectedBytes = bytesPerRow * projectedRows;

        _output.WriteLine($"measured: {bytesForSimulatedWindow} bytes / {rowsWritten} rows = {bytesPerRow:F1} bytes/row");
        _output.WriteLine(
            $"projected {MetricsHistory.RetentionDays}-day cpu+memory size " +
            $"at cap={MetricsHistory.TopAppsPerSample}: {projectedBytes / 1_000_000.0:F1} MB ({projectedRows} rows)");

        Assert.True(projectedBytes < CpuMemBudgetBytes,
            $"projected {MetricsHistory.RetentionDays}-day storage {projectedBytes / 1_000_000.0:F1} MB exceeds the budget");
    }

    /// <summary>
    /// app_storage_seconds has no gpu dimension and, like cpu/memory, is
    /// realistically saturated every tick - a live process reads its disk I/O
    /// counters unconditionally, so a mostly-idle box can still show many
    /// processes above AppUsageEpsilon (any nonzero read+write byte count).
    /// Measured separately from the cpu+mem projection above since it is its
    /// own narrow table (value_bps, not value_x10).
    /// </summary>
    [Fact]
    public void SevenDayProjection_StorageAtTopAppsPerSampleCap_StaysUnderTheStorageBudget()
    {
        const int simulatedTicks = 500;
        const int distinctApps = 100; // > TopAppsPerSample, so every tick saturates the cap.
        var random = new Random(1);
        // Appended as one batch: same rows and same on-disk bytes as one call
        // per tick, without paying a segment write per tick in a test that only
        // measures size.
        var ticks = new List<AppUsageTick>(simulatedTicks);

        for (var t = 0; t < simulatedTicks; t++)
        {
            var ts = 1_000_000 + t * (long)MetricsHistory.AppSampleIntervalSeconds;
            var storageApps = Enumerable.Range(0, distinctApps)
                .OrderBy(_ => random.Next())
                .Take(MetricsHistory.TopAppsPerSample)
                .Select(i => new AppUsagePoint($"app{i}.exe", random.Next(1, 50_000_000), null))
                .ToList();

            ticks.Add(new AppUsageTick(ts, new[] { new AppMetricSample("storage", storageApps) }));
        }

        Store.Append(ticks, null);

        var bytesForSimulatedWindow = MeasureStorageBytes();
        var rowsWritten = (long)simulatedTicks * MetricsHistory.TopAppsPerSample;
        var bytesPerRow = bytesForSimulatedWindow / (double)rowsWritten;

        var ticksPerDay = 86_400 / MetricsHistory.AppSampleIntervalSeconds;
        var projectedRows = (long)ticksPerDay * MetricsHistory.TopAppsPerSample * MetricsHistory.RetentionDays;
        var projectedBytes = bytesPerRow * projectedRows;

        _output.WriteLine($"measured: {bytesForSimulatedWindow} bytes / {rowsWritten} rows = {bytesPerRow:F1} bytes/row");
        _output.WriteLine(
            $"projected {MetricsHistory.RetentionDays}-day app_storage_seconds size " +
            $"at cap={MetricsHistory.TopAppsPerSample}: {projectedBytes / 1_000_000.0:F1} MB ({projectedRows} rows)");

        Assert.True(projectedBytes < StorageBudgetBytes,
            $"projected {MetricsHistory.RetentionDays}-day storage {projectedBytes / 1_000_000.0:F1} MB exceeds the budget");
    }

    /// <summary>
    /// app_vram_seconds carries the same gpu dimension as app_gpu_seconds and
    /// is excluded from the strict budget projection above for the same
    /// reason: concurrent GPU-active processes are bounded well below
    /// TopAppsPerSample in practice. This measures both the worst case (every
    /// tick saturates the cap, one row per app per adapter) and a realistic
    /// concurrent-app count, so the actual per-app storage add is known
    /// rather than assumed.
    /// </summary>
    [Fact]
    public void VramRow_MeasuresWorstCaseAndRealisticDailyStorage()
    {
        const int simulatedTicks = 500;
        var random = new Random(1);
        // Appended as one batch: same rows and same on-disk bytes as one call
        // per tick, without paying a segment write per tick in a test that only
        // measures size.
        var ticks = new List<AppUsageTick>(simulatedTicks);

        var scalarSample = new MetricSample(1_000_000, null, null, null, null, null,
            new[] { new GpuReading("gpu-0", "RTX 5080", "", 50, 60) }, Array.Empty<FanReading>());
        AppendScalar(scalarSample);

        for (var t = 0; t < simulatedTicks; t++)
        {
            var ts = 1_000_000 + t * (long)MetricsHistory.AppSampleIntervalSeconds;
            var vramApps = Enumerable.Range(0, MetricsHistory.TopAppsPerSample)
                .Select(i => new AppUsagePoint($"app{i}.exe", random.Next(1, 20_000), null))
                .ToList();
            ticks.Add(new AppUsageTick(ts, new[] { new AppMetricSample("vram:gpu-0", vramApps) }));
        }

        Store.Append(ticks, null);

        var bytesForSimulatedWindow = MeasureStorageBytes();
        var rowsWritten = (long)simulatedTicks * MetricsHistory.TopAppsPerSample;
        var bytesPerRow = bytesForSimulatedWindow / (double)rowsWritten;

        var ticksPerDay = 86_400 / MetricsHistory.AppSampleIntervalSeconds;

        var worstCaseRowsPerDay = (long)ticksPerDay * MetricsHistory.TopAppsPerSample;
        var worstCaseBytesPerDay = bytesPerRow * worstCaseRowsPerDay;

        const int realisticConcurrentGpuApps = 8;
        var realisticRowsPerDay = (long)ticksPerDay * realisticConcurrentGpuApps;
        var realisticBytesPerDay = bytesPerRow * realisticRowsPerDay;

        _output.WriteLine($"measured: {bytesForSimulatedWindow} bytes / {rowsWritten} rows = {bytesPerRow:F1} bytes/row");
        _output.WriteLine(
            $"worst case (every tick saturates cap={MetricsHistory.TopAppsPerSample}): " +
            $"{worstCaseBytesPerDay / 1_000_000.0:F2} MB/day, " +
            $"{worstCaseBytesPerDay * MetricsHistory.RetentionDays / 1_000_000.0:F1} MB over {MetricsHistory.RetentionDays} days");
        _output.WriteLine(
            $"realistic ({realisticConcurrentGpuApps} concurrent GPU-active apps): " +
            $"{realisticBytesPerDay / 1_000.0:F1} KB/day, " +
            $"{realisticBytesPerDay * MetricsHistory.RetentionDays / 1_000_000.0:F2} MB over {MetricsHistory.RetentionDays} days");

        Assert.True(worstCaseBytesPerDay * MetricsHistory.RetentionDays < VramWorstCaseBudgetBytes,
            $"projected {MetricsHistory.RetentionDays}-day worst-case storage " +
            $"{worstCaseBytesPerDay * MetricsHistory.RetentionDays / 1_000_000.0:F1} MB exceeds the budget");
    }
}

public sealed class BinaryAppUsageStorageEstimateTests : AppUsageStorageEstimateSpec
{
    public BinaryAppUsageStorageEstimateTests(ITestOutputHelper output) : base(output) { }

    protected override IAppUsageHistoryStore CreateStore(string dir) => new BinaryMetricsHistoryStore(dir);

    protected override long MeasureStorageBytes() =>
        new DirectoryInfo(Path.Combine(Dir, "apps")).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);

    // A regression back toward a wider per-row encoding fails this long
    // before it would approach these ceilings - tight relative to the
    // measured footprint, not a loose upper bound.
    protected override long CpuMemBudgetBytes => 150_000_000;
    protected override long VramWorstCaseBudgetBytes => 100_000_000;
    protected override long StorageBudgetBytes => 120_000_000;
}
