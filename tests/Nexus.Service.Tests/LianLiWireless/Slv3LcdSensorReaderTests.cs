using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Sensors;

namespace Nexus.Service.Tests.LianLiWireless;

public class Slv3LcdSensorReaderTests
{
    private sealed class StubSensors : ISensorProvider
    {
        public IReadOnlyList<HardwareSensor> CpuSensors { get; init; } = Array.Empty<HardwareSensor>();
        public IReadOnlyList<GpuReadout> Gpus { get; init; } = Array.Empty<GpuReadout>();
        public IReadOnlyList<HardwareSensor> MotherboardSensors { get; init; } = Array.Empty<HardwareSensor>();

        public string GetCpuModel() => "TestCPU";
        public IReadOnlyList<HardwareSensor> GetCpuSensors() => CpuSensors;
        public (bool Healthy, float DistanceToTJMax) GetCpuHealth() => (true, 20f);
        public IReadOnlyList<string> GetGpuModels() => Array.Empty<string>();
        public IReadOnlyList<HardwareSensor> GetGpuSensors() => Array.Empty<HardwareSensor>();
        public IReadOnlyList<GpuReadout> GetGpus() => Gpus;
        public IReadOnlyList<HardwareSensor> GetMemorySensors() => Array.Empty<HardwareSensor>();
        public string GetMemoryTotalFormatted() => "32 GB";
        public string GetRamBrandModel() => "";
        public IReadOnlyDictionary<string, StorageComponent> GetStorageComponents() => new Dictionary<string, StorageComponent>();
        public IReadOnlyList<string> GetStoragePartitions() => Array.Empty<string>();
        public IReadOnlyList<StorageDriveInfo> GetStorageInfo() => Array.Empty<StorageDriveInfo>();
        public string GetStorageBrandModel() => "";
        public IReadOnlyList<HardwareSensor> GetMotherboardSensors() => MotherboardSensors;
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
        Parent = new SensorParent { Id = "test", Name = "test" },
    };

    [Fact]
    public void Read_cpuLoad_finds_the_CPU_Total_load_sensor()
    {
        var reader = new Slv3LcdSensorReader(new StubSensors
        {
            CpuSensors = new[] { MakeSensor("CPU Total", "Load", 42f), MakeSensor("CPU Core #1", "Load", 10f) },
        });

        var reading = reader.Read("cpuLoad", null);

        Assert.Equal(42f, reading.Value);
        Assert.Equal("CPU", reading.Label);
        Assert.Equal("%", reading.Unit);
    }

    [Fact]
    public void Read_cpuTemp_defaults_to_celsius()
    {
        var reader = new Slv3LcdSensorReader(new StubSensors
        {
            CpuSensors = new[] { MakeSensor("Package", "Temperature", 55f) },
        });

        var reading = reader.Read("cpuTemp", null);

        Assert.Equal(55f, reading.Value);
        Assert.Equal("°C", reading.Unit);
    }

    [Fact]
    public void Read_cpuTemp_converts_to_fahrenheit_when_requested()
    {
        var reader = new Slv3LcdSensorReader(new StubSensors
        {
            CpuSensors = new[] { MakeSensor("Package", "Temperature", 55f) },
        });

        var reading = reader.Read("cpuTemp", "f");

        Assert.Equal(131f, reading.Value);
        Assert.Equal("°F", reading.Unit);
    }

    [Fact]
    public void Read_unknown_or_missing_source_defaults_to_cpuTemp()
    {
        var reader = new Slv3LcdSensorReader(new StubSensors
        {
            CpuSensors = new[] { MakeSensor("Package", "Temperature", 60f) },
        });

        Assert.Equal(60f, reader.Read(null, null).Value);
        Assert.Equal(60f, reader.Read("not-a-source", null).Value);
    }

    [Fact]
    public void Read_fanRpm_picks_the_fastest_motherboard_fan_sensor()
    {
        var reader = new Slv3LcdSensorReader(new StubSensors
        {
            MotherboardSensors = new[]
            {
                MakeSensor("Fan #1", "Fan", 900f),
                MakeSensor("Fan #2", "Fan", 1500f),
                MakeSensor("System Temperature", "Temperature", 40f),
            },
        });

        var reading = reader.Read("fanRpm", null);

        Assert.Equal(1500f, reading.Value);
        Assert.Equal("FAN", reading.Label);
        Assert.Equal("RPM", reading.Unit);
    }

    [Fact]
    public void Read_gpuLoad_and_gpuTemp_read_zero_when_no_gpu_is_present()
    {
        var reader = new Slv3LcdSensorReader(new StubSensors());

        Assert.Equal(0f, reader.Read("gpuLoad", null).Value);
        Assert.Equal(0f, reader.Read("gpuTemp", null).Value);
    }

    [Fact]
    public void Read_gpuLoad_prefers_the_first_discrete_gpu()
    {
        var reader = new Slv3LcdSensorReader(new StubSensors
        {
            Gpus = new[]
            {
                new GpuReadout { Name = "iGPU", Integrated = true, Sensors = new List<HardwareSensor> { MakeSensor("GPU Core", "Load", 5f) } },
                new GpuReadout { Name = "dGPU", Integrated = false, Sensors = new List<HardwareSensor> { MakeSensor("GPU Core", "Load", 77f) } },
            },
        });

        var reading = reader.Read("gpuLoad", null);

        Assert.Equal(77f, reading.Value);
        Assert.Equal("GPU", reading.Label);
    }
}
