using System;
using System.IO;
using System.Linq;
using Nexus.Service.Monitoring.History;
using Xunit;
using Xunit.Abstractions;

namespace Nexus.Service.Tests.Monitoring.History;

/// <summary>
/// Measures the real app_cpu_seconds/app_mem_seconds on-disk footprint at
/// MetricsHistory.TopAppsPerSample against a fully-saturated tick (every
/// slot filled, the worst case for memory - almost every live process has a
/// nonzero reading, so the epsilon filter alone rarely shrinks that table),
/// then projects it to a full MetricsHistory.RetentionDays window. This is
/// the evidence behind the cap chosen on TopAppsPerSample's doc comment: a
/// bounded number of ticks is simulated (not a literal RetentionDays' worth)
/// and the measured bytes/row scaled linearly, since SQLite's per-row cost
/// is constant for this narrow, fixed-width WITHOUT ROWID schema.
///
/// app_gpu_seconds is not included in this projection: concurrent
/// GPU-active processes are bounded by how many actually use the GPU at
/// once, which in practice stays far below this cap regardless of adapter
/// count, unlike cpu/memory where a saturated tick is the realistic case.
/// </summary>
public sealed class AppUsageStorageEstimateTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly SqliteMetricsHistoryStore _store;
    private readonly ITestOutputHelper _output;

    public AppUsageStorageEstimateTests(ITestOutputHelper output)
    {
        _output = output;
        _dir = Path.Combine(Path.GetTempPath(), "nexus-appusagestorage-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "metrics.db");
        _store = new SqliteMetricsHistoryStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void SevenDayProjection_AtTopAppsPerSampleCap_StaysUnderTheStorageBudget()
    {
        const int simulatedTicks = 500;
        const int distinctApps = 100; // > TopAppsPerSample, so every tick saturates the cap.
        var random = new Random(1);

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

            var tick = new AppUsageTick(ts, new[]
            {
                new AppMetricSample("cpu", cpuApps),
                new AppMetricSample("memory", memApps),
            });
            _store.Append(new[] { tick }, null);
        }

        var bytesForSimulatedWindow = new FileInfo(_dbPath).Length;
        var rowsWritten = (long)simulatedTicks * MetricsHistory.TopAppsPerSample * 2; // cpu + memory tables
        var bytesPerRow = bytesForSimulatedWindow / (double)rowsWritten;

        var ticksPerDay = 86_400 / MetricsHistory.AppSampleIntervalSeconds;
        var projectedRows = (long)ticksPerDay * MetricsHistory.TopAppsPerSample * 2 * MetricsHistory.RetentionDays;
        var projectedBytes = bytesPerRow * projectedRows;

        _output.WriteLine($"measured: {bytesForSimulatedWindow} bytes / {rowsWritten} rows = {bytesPerRow:F1} bytes/row");
        _output.WriteLine(
            $"projected {MetricsHistory.RetentionDays}-day app_cpu_seconds+app_mem_seconds size " +
            $"at cap={MetricsHistory.TopAppsPerSample}: {projectedBytes / 1_000_000.0:F1} MB ({projectedRows} rows)");

        // A generous ceiling (comfortably above the measured projection at
        // this cap) so the test fails loudly if TopAppsPerSample is ever
        // raised again without re-checking the storage cost, rather than on
        // ordinary measurement noise.
        Assert.True(projectedBytes < 800_000_000,
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

        for (var t = 0; t < simulatedTicks; t++)
        {
            var ts = 1_000_000 + t * (long)MetricsHistory.AppSampleIntervalSeconds;
            var storageApps = Enumerable.Range(0, distinctApps)
                .OrderBy(_ => random.Next())
                .Take(MetricsHistory.TopAppsPerSample)
                .Select(i => new AppUsagePoint($"app{i}.exe", random.Next(1, 50_000_000), null))
                .ToList();

            var tick = new AppUsageTick(ts, new[] { new AppMetricSample("storage", storageApps) });
            _store.Append(new[] { tick }, null);
        }

        var bytesForSimulatedWindow = new FileInfo(_dbPath).Length;
        var rowsWritten = (long)simulatedTicks * MetricsHistory.TopAppsPerSample;
        var bytesPerRow = bytesForSimulatedWindow / (double)rowsWritten;

        var ticksPerDay = 86_400 / MetricsHistory.AppSampleIntervalSeconds;
        var projectedRows = (long)ticksPerDay * MetricsHistory.TopAppsPerSample * MetricsHistory.RetentionDays;
        var projectedBytes = bytesPerRow * projectedRows;

        _output.WriteLine($"measured: {bytesForSimulatedWindow} bytes / {rowsWritten} rows = {bytesPerRow:F1} bytes/row");
        _output.WriteLine(
            $"projected {MetricsHistory.RetentionDays}-day app_storage_seconds size " +
            $"at cap={MetricsHistory.TopAppsPerSample}: {projectedBytes / 1_000_000.0:F1} MB ({projectedRows} rows)");

        // Same generous-ceiling intent as the cpu+mem projection: fails
        // loudly if TopAppsPerSample is raised without re-checking this
        // table's own storage cost too.
        Assert.True(projectedBytes < 800_000_000,
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

        var scalarSample = new MetricSample(1_000_000, null, null, null, null, null,
            new[] { new GpuReading("gpu-0", "RTX 5080", "", 50, 60) }, Array.Empty<FanReading>());
        _store.Append(new[] { scalarSample }, null);

        for (var t = 0; t < simulatedTicks; t++)
        {
            var ts = 1_000_000 + t * (long)MetricsHistory.AppSampleIntervalSeconds;
            var vramApps = Enumerable.Range(0, MetricsHistory.TopAppsPerSample)
                .Select(i => new AppUsagePoint($"app{i}.exe", random.Next(1, 20_000), null))
                .ToList();
            _store.Append(new[] { new AppUsageTick(ts, new[] { new AppMetricSample("vram:gpu-0", vramApps) }) }, null);
        }

        var bytesForSimulatedWindow = new FileInfo(_dbPath).Length;
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

        // Same generous-ceiling intent as the cpu+mem projection above: this
        // fails loudly if TopAppsPerSample is raised without re-checking the
        // vram table's worst-case cost too.
        Assert.True(worstCaseBytesPerDay * MetricsHistory.RetentionDays < 800_000_000,
            $"projected {MetricsHistory.RetentionDays}-day worst-case storage " +
            $"{worstCaseBytesPerDay * MetricsHistory.RetentionDays / 1_000_000.0:F1} MB exceeds the budget");
    }
}
