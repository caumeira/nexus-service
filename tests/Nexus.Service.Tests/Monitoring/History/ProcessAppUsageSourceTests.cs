using System;
using System.Collections.Generic;
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

namespace Nexus.Service.Tests.Monitoring.History;

public class ProcessAppUsageSourceTests
{
    private sealed class StubSensorProvider : ISensorProvider
    {
        public List<GpuReadout> Gpus { get; } = new();

        public string GetCpuModel() => "Stub CPU";
        public IReadOnlyList<HardwareSensor> GetCpuSensors() => Array.Empty<HardwareSensor>();
        public (bool Healthy, float DistanceToTJMax) GetCpuHealth() => (true, 20f);
        public IReadOnlyList<string> GetGpuModels() => Gpus.Select(g => g.Name).ToList();
        public IReadOnlyList<HardwareSensor> GetGpuSensors() => Array.Empty<HardwareSensor>();
        public IReadOnlyList<GpuReadout> GetGpus() => Gpus;
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

    private static ProcessInfo Proc(string name, double cpu, double mem, int pid = 0) =>
        new() { Pid = pid, Name = name, CpuPercent = cpu, MemoryMb = mem };

    private static (ProcessAppUsageSource Source, ProcessMonitor Processes, GpuProcessMonitor GpuProcesses, StubSensorProvider Sensors) Build()
    {
        var hub = new MultiplexHub();
        var processes = new ProcessMonitor(hub);
        var gpuProcesses = new GpuProcessMonitor(hub);
        var sensors = new StubSensorProvider();
        var source = new ProcessAppUsageSource(processes, gpuProcesses, sensors);
        return (source, processes, gpuProcesses, sensors);
    }

    [Fact]
    public void Sample_ReturnsEmpty_WhenNoProcessesOrGpuEntries()
    {
        var (source, _, _, _) = Build();

        var result = source.Sample();

        Assert.Empty(result);
    }

    [Fact]
    public void Sample_AggregatesMultiplePidsWithTheSameName_ForCpu()
    {
        var (source, processes, _, _) = Build();
        processes.SetProcessesForTest(new[]
        {
            Proc("chrome", cpu: 5, mem: 100, pid: 1),
            Proc("chrome", cpu: 7, mem: 150, pid: 2),
            Proc("notepad", cpu: 1, mem: 20, pid: 3),
        });

        var cpu = source.Sample().Single(m => m.Metric == "cpu");

        var chrome = cpu.Apps.Single(a => a.Name == "chrome");
        Assert.Equal(12, chrome.Value);
    }

    [Fact]
    public void Sample_ReturnsMemoryMetric_SortedByMemoryMb_IndependentlyOfCpuOrder()
    {
        var (source, processes, _, _) = Build();
        processes.SetProcessesForTest(new[]
        {
            Proc("low-mem-high-cpu", cpu: 90, mem: 10),
            Proc("high-mem-low-cpu", cpu: 1, mem: 900),
        });

        var mem = source.Sample().Single(m => m.Metric == "memory");

        Assert.Equal("high-mem-low-cpu", mem.Apps.First().Name);
    }

    [Fact]
    public void Sample_LimitsEachMetricToTopAppsPerSample()
    {
        var (source, processes, _, _) = Build();
        var procs = Enumerable.Range(1, MetricsHistory.TopAppsPerSample + 5)
            .Select(i => Proc($"app{i}", cpu: i, mem: i))
            .ToList();
        processes.SetProcessesForTest(procs);

        var cpu = source.Sample().Single(m => m.Metric == "cpu");

        Assert.Equal(MetricsHistory.TopAppsPerSample, cpu.Apps.Count);
        // The highest-cpu app (last one generated) wins the top slot.
        Assert.Equal($"app{MetricsHistory.TopAppsPerSample + 5}", cpu.Apps.First().Name);
    }

    [Fact]
    public void Sample_RecordsEveryMidRankApp_UpToTheRaisedCap()
    {
        // A count comfortably above the previous cutoff but under the
        // current cap: every one of them must appear, not just the apps
        // that would have ranked above that previous cutoff - this is the
        // gap the raised cap closes for a mid-ranked, intermittently-
        // fluctuating process.
        var (source, processes, _, _) = Build();
        var procs = Enumerable.Range(1, 30)
            .Select(i => Proc($"app{i}", cpu: i, mem: i))
            .ToList();
        processes.SetProcessesForTest(procs);

        var cpu = source.Sample().Single(m => m.Metric == "cpu");

        Assert.Equal(30, cpu.Apps.Count);
        Assert.Contains(cpu.Apps, a => a.Name == "app15");
    }

    [Fact]
    public void Sample_ExcludesAnAppWithExactlyZeroCpu_EvenWithRoomUnderTheCap()
    {
        var (source, processes, _, _) = Build();
        processes.SetProcessesForTest(new[]
        {
            Proc("idle.exe", cpu: 0, mem: 10),
            Proc("active.exe", cpu: 5, mem: 10),
        });

        var cpu = source.Sample().Single(m => m.Metric == "cpu");

        Assert.DoesNotContain(cpu.Apps, a => a.Name == "idle.exe");
        Assert.Contains(cpu.Apps, a => a.Name == "active.exe");
    }

    [Fact]
    public void Sample_ExcludesAnAppWithExactlyZeroMemory_EvenWithRoomUnderTheCap()
    {
        var (source, processes, _, _) = Build();
        processes.SetProcessesForTest(new[]
        {
            Proc("empty.exe", cpu: 1, mem: 0),
            Proc("active.exe", cpu: 1, mem: 10),
        });

        var mem = source.Sample().Single(m => m.Metric == "memory");

        Assert.DoesNotContain(mem.Apps, a => a.Name == "empty.exe");
        Assert.Contains(mem.Apps, a => a.Name == "active.exe");
    }

    [Fact]
    public void Sample_MapsGpuEntries_ToMetricId_ViaMatchingAdapterLuid()
    {
        var (source, _, gpuProcesses, sensors) = Build();
        sensors.Gpus.Add(new GpuReadout { Id = "/gpu-nvidia/0", Name = "RTX 5080", AdapterLuid = "10:20" });
        gpuProcesses.SetSnapshotForTest(new[]
        {
            new GpuProcessEntry { Name = "game.exe", GpuPercent = 40, DedicatedMb = 2048, AdapterLuid = "10:20" },
        });

        var gpu = source.Sample().Single(m => m.Metric == "gpu:gpu-nvidia-0");

        var entry = gpu.Apps.Single();
        Assert.Equal("game.exe", entry.Name);
        Assert.Equal(40, entry.Value);
        Assert.Equal(2048, entry.VramMb);
    }

    [Fact]
    public void Sample_SkipsGpuEntries_WhenAdapterLuidHasNoMatchingScalarGpu()
    {
        var (source, _, gpuProcesses, sensors) = Build();
        sensors.Gpus.Add(new GpuReadout { Id = "/gpu-nvidia/0", Name = "RTX 5080", AdapterLuid = "10:20" });
        gpuProcesses.SetSnapshotForTest(new[]
        {
            new GpuProcessEntry { Name = "game.exe", GpuPercent = 40, DedicatedMb = 2048, AdapterLuid = "99:99" },
        });

        var result = source.Sample();

        Assert.DoesNotContain(result, m => m.Metric.StartsWith("gpu:", StringComparison.Ordinal));
    }

    [Fact]
    public void Sample_MapsGpuEntries_ToVramMetricId_ViaMatchingAdapterLuid()
    {
        var (source, _, gpuProcesses, sensors) = Build();
        sensors.Gpus.Add(new GpuReadout { Id = "/gpu-nvidia/0", Name = "RTX 5080", AdapterLuid = "10:20" });
        gpuProcesses.SetSnapshotForTest(new[]
        {
            new GpuProcessEntry { Name = "game.exe", GpuPercent = 40, DedicatedMb = 2048, AdapterLuid = "10:20" },
        });

        var vram = source.Sample().Single(m => m.Metric == "vram:gpu-nvidia-0");

        var entry = vram.Apps.Single();
        Assert.Equal("game.exe", entry.Name);
        Assert.Equal(2048, entry.Value);
        Assert.Null(entry.VramMb);
    }

    [Fact]
    public void Sample_RanksVramMetric_ByDedicatedMb_IndependentlyOfGpuPercent()
    {
        // "idle-hog" barely touches the GPU but holds far more VRAM than
        // "busy-light" - the vram ranking must put it first even though the
        // gpu (load) ranking would not.
        var (source, _, gpuProcesses, sensors) = Build();
        sensors.Gpus.Add(new GpuReadout { Id = "/gpu-nvidia/0", Name = "RTX 5080", AdapterLuid = "10:20" });
        gpuProcesses.SetSnapshotForTest(new[]
        {
            new GpuProcessEntry { Name = "idle-hog", GpuPercent = 1, DedicatedMb = 4096, AdapterLuid = "10:20" },
            new GpuProcessEntry { Name = "busy-light", GpuPercent = 50, DedicatedMb = 100, AdapterLuid = "10:20" },
        });

        var vram = source.Sample().Single(m => m.Metric == "vram:gpu-nvidia-0");
        var gpu = source.Sample().Single(m => m.Metric == "gpu:gpu-nvidia-0");

        Assert.Equal("idle-hog", vram.Apps.First().Name);
        Assert.Equal("busy-light", gpu.Apps.First().Name);
    }

    [Fact]
    public void Sample_ExcludesAnEntryWithExactlyZeroDedicatedMb_FromTheVramMetric()
    {
        var (source, _, gpuProcesses, sensors) = Build();
        sensors.Gpus.Add(new GpuReadout { Id = "/gpu-nvidia/0", Name = "RTX 5080", AdapterLuid = "10:20" });
        gpuProcesses.SetSnapshotForTest(new[]
        {
            new GpuProcessEntry { Name = "no-vram.exe", GpuPercent = 5, DedicatedMb = 0, AdapterLuid = "10:20" },
            new GpuProcessEntry { Name = "some-vram.exe", GpuPercent = 5, DedicatedMb = 10, AdapterLuid = "10:20" },
        });

        var vram = source.Sample().Single(m => m.Metric == "vram:gpu-nvidia-0");

        Assert.DoesNotContain(vram.Apps, a => a.Name == "no-vram.exe");
        Assert.Contains(vram.Apps, a => a.Name == "some-vram.exe");
    }

    [Fact]
    public void Sample_SkipsVramEntries_WhenAdapterLuidHasNoMatchingScalarGpu()
    {
        var (source, _, gpuProcesses, sensors) = Build();
        sensors.Gpus.Add(new GpuReadout { Id = "/gpu-nvidia/0", Name = "RTX 5080", AdapterLuid = "10:20" });
        gpuProcesses.SetSnapshotForTest(new[]
        {
            new GpuProcessEntry { Name = "game.exe", GpuPercent = 40, DedicatedMb = 2048, AdapterLuid = "99:99" },
        });

        var result = source.Sample();

        Assert.DoesNotContain(result, m => m.Metric.StartsWith("vram:", StringComparison.Ordinal));
    }
}
