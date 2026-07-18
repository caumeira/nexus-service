using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Activity;
using Nexus.Service.Diagnostics.Storage;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Sensors;
using Nexus.Service.Sockets;
using Xunit;
using Xunit.Abstractions;

namespace Nexus.Service.Tests.Monitoring.History;

/// <summary>
/// Measures ProcessAppUsageSource.Sample()'s wall-clock cost with the added
/// third (storage) per-metric reduction pass, at a process count well above
/// what a real box carries - proving the extra AppMetricSample block does
/// not meaningfully regress the per-tick reduction cost this class runs
/// every MetricsHistory.AppSampleIntervalSeconds. Category=Manual: see
/// ProcessMonitorPerformanceTests for why timing tests stay out of the
/// default gate.
/// </summary>
[Trait("Category", "Manual")]
public class ProcessAppUsageSourcePerformanceTests
{
    private readonly ITestOutputHelper _output;

    public ProcessAppUsageSourcePerformanceTests(ITestOutputHelper output) => _output = output;

    private sealed class StubSensorProvider : ISensorProvider
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

    [Fact]
    public void Sample_StaysUnderBudget_AtARealisticWorstCaseProcessCount()
    {
        var hub = new MultiplexHub();
        var processes = new ProcessMonitor(hub);
        var gpuProcesses = new GpuProcessMonitor(hub);
        var source = new ProcessAppUsageSource(processes, gpuProcesses, new StubSensorProvider());

        const int processCount = 400; // comfortably above a busy real box's live process count
        var random = new Random(1);
        var procs = Enumerable.Range(0, processCount)
            .Select(i => new ProcessInfo
            {
                Pid = i,
                Name = $"app{i}.exe",
                CpuPercent = random.Next(0, 100),
                MemoryMb = random.Next(1, 2000),
                StorageBytesPerSec = random.Next(0, 50_000_000),
            })
            .ToList();
        processes.SetProcessesForTest(procs);

        // "before": a warm-up call so JIT/allocation costs from the first
        // invocation do not skew the measured tick below.
        var before = Stopwatch.StartNew();
        var warmup = source.Sample();
        before.Stop();

        var after = Stopwatch.StartNew();
        var result = source.Sample();
        after.Stop();

        _output.WriteLine($"before (warm-up tick): {before.ElapsedMilliseconds}ms, {warmup.Count} metric samples");
        _output.WriteLine($"after (measured tick): {after.ElapsedMilliseconds}ms, {result.Count} metric samples, {processCount} processes");

        Assert.Equal(3, result.Count); // cpu, memory, storage
        Assert.True(after.ElapsedMilliseconds < 100,
            $"Sample() took {after.ElapsedMilliseconds}ms for {processCount} processes, exceeding the budget");
    }
}
