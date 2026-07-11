using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Sensors;
using Xunit;

namespace Nexus.Service.Tests.Sensors;

public class SensorSnapshotResolverTests
{
    private sealed class StubSensors : ISensorProvider
    {
        public IReadOnlyList<HardwareSensor> CpuSensors { get; init; } = Array.Empty<HardwareSensor>();
        public IReadOnlyList<GpuReadout> Gpus { get; init; } = Array.Empty<GpuReadout>();
        public IReadOnlyList<HardwareSensor> MemorySensors { get; init; } = Array.Empty<HardwareSensor>();
        public IReadOnlyList<HardwareSensor> MotherboardSensors { get; init; } = Array.Empty<HardwareSensor>();
        public IReadOnlyDictionary<string, StorageComponent> StorageComponents { get; init; } = new Dictionary<string, StorageComponent>();
        public List<HardwareComponent> Nics { get; init; } = new();

        public string GetCpuModel() => "TestCPU";
        public IReadOnlyList<HardwareSensor> GetCpuSensors() => CpuSensors;
        public (bool Healthy, float DistanceToTJMax) GetCpuHealth() => (true, 20f);
        public IReadOnlyList<string> GetGpuModels() => Array.Empty<string>();
        public IReadOnlyList<HardwareSensor> GetGpuSensors() => Array.Empty<HardwareSensor>();
        public IReadOnlyList<GpuReadout> GetGpus() => Gpus;
        public IReadOnlyList<HardwareSensor> GetMemorySensors() => MemorySensors;
        public string GetMemoryTotalFormatted() => "32 GB";
        public string GetRamBrandModel() => "";
        public IReadOnlyDictionary<string, StorageComponent> GetStorageComponents(bool includeSmart = true) => StorageComponents;
        public IReadOnlyList<string> GetStoragePartitions() => Array.Empty<string>();
        public IReadOnlyList<StorageDriveInfo> GetStorageInfo() => Array.Empty<StorageDriveInfo>();
        public string GetStorageBrandModel() => "";
        public IReadOnlyList<HardwareSensor> GetMotherboardSensors() => MotherboardSensors;
        public string GetMotherboardModel() => "TestMobo";
        public SensorExtras GetSensorExtras() => new() { Nics = Nics };
        public string GetOsVersion() => "TestOS";
        public void SetPollingRate(int pollingRate) { }
        public Task ReadyAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static HardwareSensor MakeSensor(string id, string type, float value) => new()
    {
        Id = id,
        Name = id,
        Type = type,
        Value = value,
        Units = "",
        Formatted = value.ToString(),
        Parent = new SensorParent { Id = "test", Name = "test" },
    };

    [Fact]
    public void Resolve_finds_a_quick_summary_sensor()
    {
        var sensors = new StubSensors { CpuSensors = new[] { MakeSensor("cpu/package", "Temperature", 55f) } };

        var found = SensorSnapshotResolver.Resolve(sensors, "quick", "summary/cpu-temp");

        Assert.NotNull(found);
        Assert.Equal(55f, found!.Value);
    }

    [Fact]
    public void Resolve_finds_a_cpu_sensor()
    {
        var sensors = new StubSensors { CpuSensors = new[] { MakeSensor("cpu/core0", "Load", 12f) } };

        var found = SensorSnapshotResolver.Resolve(sensors, "cpu", "cpu/core0");

        Assert.NotNull(found);
        Assert.Equal(12f, found!.Value);
    }

    [Fact]
    public void Resolve_finds_a_gpu_sensor_on_the_only_gpu()
    {
        var sensors = new StubSensors
        {
            Gpus = new[] { new GpuReadout { Name = "GPU0", Sensors = new List<HardwareSensor> { MakeSensor("gpu/core", "Load", 88f) } } },
        };

        var found = SensorSnapshotResolver.Resolve(sensors, "gpu", "gpu/core");

        Assert.NotNull(found);
        Assert.Equal(88f, found!.Value);
    }

    [Fact]
    public void Resolve_matches_a_gpu_sensor_id_across_multiple_gpus()
    {
        var sensors = new StubSensors
        {
            Gpus = new[]
            {
                new GpuReadout { Name = "GPU0", Integrated = true, Sensors = new List<HardwareSensor> { MakeSensor("gpu0/core", "Load", 10f) } },
                new GpuReadout { Name = "GPU1", Integrated = false, Sensors = new List<HardwareSensor> { MakeSensor("gpu1/core", "Load", 77f) } },
            },
        };

        var fromFirst = SensorSnapshotResolver.Resolve(sensors, "gpu", "gpu0/core");
        var fromSecond = SensorSnapshotResolver.Resolve(sensors, "gpu", "gpu1/core");

        Assert.NotNull(fromFirst);
        Assert.Equal(10f, fromFirst!.Value);
        Assert.NotNull(fromSecond);
        Assert.Equal(77f, fromSecond!.Value);
    }

    [Fact]
    public void Resolve_finds_a_memory_sensor()
    {
        var sensors = new StubSensors { MemorySensors = new[] { MakeSensor("mem/used", "Data", 12.3f) } };

        var found = SensorSnapshotResolver.Resolve(sensors, "memory", "mem/used");

        Assert.NotNull(found);
        Assert.Equal(12.3f, found!.Value);
    }

    [Fact]
    public void Resolve_finds_a_motherboard_sensor()
    {
        var sensors = new StubSensors { MotherboardSensors = new[] { MakeSensor("mobo/temp", "Temperature", 40f) } };

        var found = SensorSnapshotResolver.Resolve(sensors, "motherboard", "mobo/temp");

        Assert.NotNull(found);
        Assert.Equal(40f, found!.Value);
    }

    [Fact]
    public void Resolve_finds_a_storage_sensor()
    {
        var sensors = new StubSensors
        {
            StorageComponents = new Dictionary<string, StorageComponent>
            {
                ["C:"] = new StorageComponent { Sensors = new List<HardwareSensor> { MakeSensor("storage/used", "Load", 63f) } },
            },
        };

        var found = SensorSnapshotResolver.Resolve(sensors, "storage", "storage/used");

        Assert.NotNull(found);
        Assert.Equal(63f, found!.Value);
    }

    [Fact]
    public void Resolve_returns_null_for_an_unknown_sensor_id()
    {
        var sensors = new StubSensors { CpuSensors = new[] { MakeSensor("cpu/core0", "Load", 12f) } };

        Assert.Null(SensorSnapshotResolver.Resolve(sensors, "cpu", "cpu/does-not-exist"));
    }

    [Fact]
    public void Resolve_returns_null_for_an_unrecognized_category()
    {
        var sensors = new StubSensors { CpuSensors = new[] { MakeSensor("cpu/core0", "Load", 12f) } };

        Assert.Null(SensorSnapshotResolver.Resolve(sensors, "fps", "cpu/core0"));
    }
}
