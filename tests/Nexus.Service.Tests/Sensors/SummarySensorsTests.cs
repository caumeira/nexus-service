using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Sensors;
using Xunit;

namespace Nexus.Service.Tests.Sensors;

public class SummarySensorsTests
{
    private sealed class StubSensors : ISensorProvider
    {
        public IReadOnlyList<HardwareSensor> CpuSensors { get; init; } = Array.Empty<HardwareSensor>();
        public IReadOnlyList<GpuReadout> Gpus { get; init; } = Array.Empty<GpuReadout>();
        public IReadOnlyList<HardwareSensor> MemorySensors { get; init; } = Array.Empty<HardwareSensor>();

        public string GetCpuModel() => "TestCPU";
        public IReadOnlyList<HardwareSensor> GetCpuSensors() => CpuSensors;
        public (bool Healthy, float DistanceToTJMax) GetCpuHealth() => (true, 20f);
        public IReadOnlyList<string> GetGpuModels() => Array.Empty<string>();
        public IReadOnlyList<HardwareSensor> GetGpuSensors() => Array.Empty<HardwareSensor>();
        public IReadOnlyList<GpuReadout> GetGpus() => Gpus;
        public IReadOnlyList<HardwareSensor> GetMemorySensors() => MemorySensors;
        public string GetMemoryTotalFormatted() => "32 GB";
        public string GetRamBrandModel() => "";
        public IReadOnlyDictionary<string, StorageComponent> GetStorageComponents() => new Dictionary<string, StorageComponent>();
        public IReadOnlyList<string> GetStoragePartitions() => Array.Empty<string>();
        public IReadOnlyList<StorageDriveInfo> GetStorageInfo() => Array.Empty<StorageDriveInfo>();
        public string GetStorageBrandModel() => "";
        public IReadOnlyList<HardwareSensor> GetMotherboardSensors() => Array.Empty<HardwareSensor>();
        public string GetMotherboardModel() => "TestMobo";
        public SensorExtras GetSensorExtras() => new();
        public string GetOsVersion() => "TestOS";
        public void SetPollingRate(int pollingRate) { }
        public Task ReadyAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static HardwareSensor MakeSensor(string name, string type, float value) => new()
    {
        Id = $"test/{name}",
        Name = name,
        Type = type,
        Value = value,
        Units = "unit",
        Formatted = "formatted",
        Min = 1f,
        Max = 2f,
        Average = 1.5f,
        Usage = 0.5f,
        TheoreticalMaximum = 3f,
        FormattedMax = "max",
        FormattedMin = "min",
        FormattedAverage = "avg",
        FormattedUsage = "usage",
        Parent = new SensorParent { Id = "test", Name = "test" },
    };

    [Fact]
    public void Build_omits_gpu_entries_when_no_gpu_is_present_but_keeps_cpu_and_memory()
    {
        var sensors = new StubSensors
        {
            CpuSensors = new[] { MakeSensor("Package", "Temperature", 55f), MakeSensor("CPU Total", "Load", 30f) },
            MemorySensors = new[] { MakeSensor("Memory", "Load", 40f) },
        };

        var result = SummarySensors.Build(sensors);

        Assert.Equal(3, result.Count);
        Assert.Equal("summary/cpu-temp", result[0].Id);
        Assert.Equal("summary/cpu-usage", result[1].Id);
        Assert.Equal("summary/memory-usage", result[2].Id);
    }

    [Fact]
    public void Build_omits_cpu_entries_when_no_cpu_sensor_is_present()
    {
        var sensors = new StubSensors
        {
            MemorySensors = new[] { MakeSensor("Memory", "Load", 40f) },
        };

        var result = SummarySensors.Build(sensors);

        Assert.Single(result);
        Assert.Equal("summary/memory-usage", result[0].Id);
    }

    [Fact]
    public void Build_returns_fixed_order_with_all_sensors_present()
    {
        var sensors = new StubSensors
        {
            CpuSensors = new[] { MakeSensor("Package", "Temperature", 55f), MakeSensor("CPU Total", "Load", 30f) },
            Gpus = new[]
            {
                new GpuReadout
                {
                    Name = "dGPU",
                    Integrated = false,
                    Sensors = new List<HardwareSensor>
                    {
                        MakeSensor("GPU Core", "Temperature", 65f),
                        MakeSensor("GPU Core", "Load", 80f),
                    },
                },
            },
            MemorySensors = new[] { MakeSensor("Memory", "Load", 40f) },
        };

        var result = SummarySensors.Build(sensors);

        Assert.Equal(5, result.Count);
        Assert.Equal(
            new[] { "summary/cpu-temp", "summary/cpu-usage", "summary/gpu-temp", "summary/gpu-usage", "summary/memory-usage" },
            result.ConvertAll(s => s.Id));
        Assert.Equal(
            new[] { "CPU Temperature", "CPU Usage", "GPU Temperature", "GPU Usage", "Memory Usage" },
            result.ConvertAll(s => s.Name));
        foreach (var sensor in result)
        {
            Assert.Equal("summary", sensor.Parent.Id);
            Assert.Equal("Quick", sensor.Parent.Name);
        }
        Assert.Equal(55f, result[0].Value);
        Assert.Equal(30f, result[1].Value);
        Assert.Equal(65f, result[2].Value);
        Assert.Equal(80f, result[3].Value);
        Assert.Equal(40f, result[4].Value);
    }

    [Fact]
    public void Build_clones_the_source_sensor_fields_onto_the_summary_entry()
    {
        var sensors = new StubSensors
        {
            CpuSensors = new[] { MakeSensor("Package", "Temperature", 55f) },
        };

        var result = SummarySensors.Build(sensors);

        var entry = Assert.Single(result);
        Assert.Equal("Temperature", entry.Type);
        Assert.Equal("unit", entry.Units);
        Assert.Equal("formatted", entry.Formatted);
        Assert.Equal(1f, entry.Min);
        Assert.Equal(2f, entry.Max);
        Assert.Equal(1.5f, entry.Average);
        Assert.Equal(0.5f, entry.Usage);
        Assert.Equal(3f, entry.TheoreticalMaximum);
        Assert.Equal("max", entry.FormattedMax);
        Assert.Equal("min", entry.FormattedMin);
        Assert.Equal("avg", entry.FormattedAverage);
        Assert.Equal("usage", entry.FormattedUsage);
    }

    [Fact]
    public void CpuTemp_prefers_the_Package_sensor_when_present()
    {
        var sensors = new StubSensors
        {
            CpuSensors = new[] { MakeSensor("Core #1", "Temperature", 40f), MakeSensor("Package", "Temperature", 55f) },
        };

        Assert.Equal(55f, SummarySensors.Value(sensors, SummarySensorKind.CpuTemp));
    }

    [Fact]
    public void CpuTemp_falls_back_to_the_first_temperature_sensor_when_no_Package_sensor_exists()
    {
        var sensors = new StubSensors
        {
            CpuSensors = new[] { MakeSensor("Core #1", "Temperature", 40f) },
        };

        Assert.Equal(40f, SummarySensors.Value(sensors, SummarySensorKind.CpuTemp));
    }

    [Fact]
    public void CpuUsage_prefers_CPU_Total_when_present()
    {
        var sensors = new StubSensors
        {
            CpuSensors = new[] { MakeSensor("CPU Core #1", "Load", 10f), MakeSensor("CPU Total", "Load", 42f) },
        };

        Assert.Equal(42f, SummarySensors.Value(sensors, SummarySensorKind.CpuUsage));
    }

    [Fact]
    public void GpuTemp_and_GpuUsage_prefer_the_first_discrete_gpu()
    {
        var sensors = new StubSensors
        {
            Gpus = new[]
            {
                new GpuReadout { Name = "iGPU", Integrated = true, Sensors = new List<HardwareSensor> { MakeSensor("GPU Core", "Temperature", 5f), MakeSensor("GPU Core", "Load", 5f) } },
                new GpuReadout { Name = "dGPU", Integrated = false, Sensors = new List<HardwareSensor> { MakeSensor("GPU Core", "Temperature", 65f), MakeSensor("GPU Core", "Load", 77f) } },
            },
        };

        Assert.Equal(65f, SummarySensors.Value(sensors, SummarySensorKind.GpuTemp));
        Assert.Equal(77f, SummarySensors.Value(sensors, SummarySensorKind.GpuUsage));
    }

    [Fact]
    public void Value_returns_null_when_the_underlying_sensor_is_absent()
    {
        var sensors = new StubSensors();

        Assert.Null(SummarySensors.Value(sensors, SummarySensorKind.CpuTemp));
        Assert.Null(SummarySensors.Value(sensors, SummarySensorKind.CpuUsage));
        Assert.Null(SummarySensors.Value(sensors, SummarySensorKind.GpuTemp));
        Assert.Null(SummarySensors.Value(sensors, SummarySensorKind.GpuUsage));
        Assert.Null(SummarySensors.Value(sensors, SummarySensorKind.MemoryUsage));
    }
}
