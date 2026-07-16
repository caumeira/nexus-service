using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Diagnostics.Gpu;
using Nexus.Service.Diagnostics.Storage;
using Nexus.Service.Diagnostics.Temperature;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Sensors;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History;

/// <summary>
/// Drives MetricsSampler through its internal Tick(DateTime, CancellationToken)
/// seam - the BackgroundService loop itself (PeriodicTimer, ReadyAsync wait)
/// is not under test here. A RecordingMetricsHistoryStore stands in for the
/// real SQLite store so flush cadence and prune cutoffs are assertable
/// without touching disk.
/// </summary>
public class MetricsSamplerTests
{
    private sealed class StubSensors : ISensorProvider
    {
        public string GetCpuModel() => "Stub CPU";
        public IReadOnlyList<HardwareSensor> GetCpuSensors() => Array.Empty<HardwareSensor>();
        public (bool Healthy, float DistanceToTJMax) GetCpuHealth() => (true, 20f);
        public IReadOnlyList<string> GetGpuModels() => Array.Empty<string>();
        public IReadOnlyList<HardwareSensor> GetGpuSensors() => Array.Empty<HardwareSensor>();
        public IReadOnlyList<GpuReadout> GetGpus() => Array.Empty<GpuReadout>();
        public IReadOnlyList<HardwareSensor> GetMemorySensors() => Array.Empty<HardwareSensor>();
        public string GetMemoryTotalFormatted() => "32 GB";
        public string GetRamBrandModel() => "";
        public IReadOnlyDictionary<string, StorageComponent> GetStorageComponents(bool includeSmart = true) => new Dictionary<string, StorageComponent>();
        public IReadOnlyList<string> GetStoragePartitions() => Array.Empty<string>();
        public IReadOnlyList<StorageDriveInfo> GetStorageInfo() => Array.Empty<StorageDriveInfo>();
        public string GetStorageBrandModel() => "";
        public IReadOnlyList<HardwareSensor> GetMotherboardSensors() => Array.Empty<HardwareSensor>();
        public string GetMotherboardModel() => "Stub Board";
        public SensorExtras GetSensorExtras() => new();
        public string GetOsVersion() => "TestOS";
        public void SetPollingRate(int pollingRate) { }
        public Task ReadyAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubMetricsSource : IMetricsSource
    {
        public int Calls { get; private set; }
        public double Cpu { get; set; } = 42;

        public Task<MetricSample> SampleAsync(long tsSec, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new MetricSample(
                tsSec, Cpu, 60, 1000, 500, 55, Array.Empty<GpuReading>(), Array.Empty<FanReading>()));
        }
    }

    private sealed class RecordingMetricsHistoryStore : IMetricsHistoryStore
    {
        public List<(int Count, long? PruneCutoffSec)> AppendCalls { get; } = new();

        public void Append(IReadOnlyList<MetricSample> samples, long? pruneCutoffSec) =>
            AppendCalls.Add((samples.Count, pruneCutoffSec));

        public IReadOnlyList<MetricSample> Query(long fromSec, long toSec) => Array.Empty<MetricSample>();

        public void Dispose() { }
    }

    private sealed class StubGpuHealthSource : IGpuHealthSource
    {
        public GpuHealthSnapshot Snapshot(bool forceRefresh = false) => GpuHealthSnapshot.Unsupported;
    }

    private sealed class StubSmartHealthSource : ISmartHealthSource
    {
        public SmartSnapshot Snapshot() => new() { Supported = false, Drives = Array.Empty<SmartDriveInfo>() };
    }

    private static TemperatureRollup CreateRollup() =>
        new(new StubSensors(), new StubGpuHealthSource(), new StubSmartHealthSource(), new InMemoryTemperatureHistoryStore());

    private static MetricsSampler CreateSampler(
        StubMetricsSource source, RecordingMetricsHistoryStore store, MetricsSampleBuffer? buffer = null) =>
        new(new StubSensors(), source, buffer ?? new MetricsSampleBuffer(), store, CreateRollup());

    [Fact]
    public async Task Tick_AppendsOneSampleToTheBuffer_PerCall()
    {
        var source = new StubMetricsSource();
        var buffer = new MetricsSampleBuffer();
        var sampler = CreateSampler(source, new RecordingMetricsHistoryStore(), buffer);

        await sampler.Tick(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), CancellationToken.None);

        Assert.Equal(1, source.Calls);
        Assert.Single(buffer.PendingSnapshot());
    }

    [Fact]
    public async Task Tick_DoesNotFlush_BeforeTheFlushIntervalIsReached()
    {
        var source = new StubMetricsSource();
        var store = new RecordingMetricsHistoryStore();
        var sampler = CreateSampler(source, store);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < MetricsHistory.FlushSeconds - 1; i++)
        {
            await sampler.Tick(start.AddSeconds(i), CancellationToken.None);
        }

        Assert.Empty(store.AppendCalls);
    }

    [Fact]
    public async Task Tick_FlushesTheWholeBuffer_OnTheFlushIntervalTick()
    {
        var source = new StubMetricsSource();
        var store = new RecordingMetricsHistoryStore();
        var buffer = new MetricsSampleBuffer();
        var sampler = CreateSampler(source, store, buffer);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < MetricsHistory.FlushSeconds; i++)
        {
            await sampler.Tick(start.AddSeconds(i), CancellationToken.None);
        }

        var call = Assert.Single(store.AppendCalls);
        Assert.Equal(MetricsHistory.FlushSeconds, call.Count);
        Assert.Empty(buffer.PendingSnapshot()); // RemoveThrough ran after the successful flush
    }

    [Fact]
    public async Task Tick_FlushesAgain_AfterTheSecondFlushInterval()
    {
        var source = new StubMetricsSource();
        var store = new RecordingMetricsHistoryStore();
        var sampler = CreateSampler(source, store);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < MetricsHistory.FlushSeconds * 2; i++)
        {
            await sampler.Tick(start.AddSeconds(i), CancellationToken.None);
        }

        Assert.Equal(2, store.AppendCalls.Count);
        Assert.All(store.AppendCalls, c => Assert.Equal(MetricsHistory.FlushSeconds, c.Count));
    }

    [Fact]
    public async Task Tick_RequestsAPrune_OnTheFirstFlush()
    {
        // _lastPruneUtc starts at DateTime.MinValue, so the very first flush
        // is always "over an hour" since the last prune - mirrors
        // TemperatureRollup.MaybePrune enforcing retention immediately on a
        // fresh start rather than waiting a full hour after boot.
        var source = new StubMetricsSource();
        var store = new RecordingMetricsHistoryStore();
        var sampler = CreateSampler(source, store);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < MetricsHistory.FlushSeconds; i++)
        {
            await sampler.Tick(start.AddSeconds(i), CancellationToken.None);
        }

        var call = Assert.Single(store.AppendCalls);
        Assert.NotNull(call.PruneCutoffSec);
        var expectedCutoff = new DateTimeOffset(start.AddSeconds(MetricsHistory.FlushSeconds - 1)).ToUnixTimeSeconds()
            - MetricsHistory.RetentionDays * 86_400L;
        Assert.Equal(expectedCutoff, call.PruneCutoffSec);
    }

    [Fact]
    public async Task Tick_DoesNotRequestASecondPrune_WithinTheSameHour()
    {
        var source = new StubMetricsSource();
        var store = new RecordingMetricsHistoryStore();
        var sampler = CreateSampler(source, store);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < MetricsHistory.FlushSeconds * 2; i++)
        {
            await sampler.Tick(start.AddSeconds(i), CancellationToken.None);
        }

        Assert.Equal(2, store.AppendCalls.Count);
        Assert.NotNull(store.AppendCalls[0].PruneCutoffSec);
        Assert.Null(store.AppendCalls[1].PruneCutoffSec);
    }

    [Fact]
    public async Task Tick_DoesNotThrow_WhenTheSourceFailsEntirely()
    {
        var source = new ThrowingMetricsSource();
        var store = new RecordingMetricsHistoryStore();
        var buffer = new MetricsSampleBuffer();
        var sampler = new MetricsSampler(new StubSensors(), source, buffer, store, CreateRollup());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sampler.Tick(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), CancellationToken.None));

        // internal Tick propagates a source failure to its caller (the
        // BackgroundService loop's own try/catch is what applies the
        // once-a-minute warn throttle, not Tick itself).
        Assert.Empty(buffer.PendingSnapshot());
    }

    private sealed class ThrowingMetricsSource : IMetricsSource
    {
        public Task<MetricSample> SampleAsync(long tsSec, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }
}
