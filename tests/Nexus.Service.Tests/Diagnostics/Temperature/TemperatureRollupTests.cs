using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Diagnostics.Gpu;
using Nexus.Service.Diagnostics.Storage;
using Nexus.Service.Diagnostics.Temperature;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Sensors;
using Xunit;
#if WINDOWS
using Nexus.Service.Platform;
#endif

namespace Nexus.Service.Tests.Diagnostics.Temperature;

/// <summary>
/// Tick-level coverage for TemperatureRollup: MetricsSampler drives Tick()
/// at 1Hz externally, so this class carries no BackgroundService of its
/// own. GpuHealthMonitor and SmartHealthMonitor are hardware-gated with no
/// test seam, so these tests run them for real: off Windows (this suite's
/// environment) both report unsupported, so the component-count assertions
/// below only exercise the CPU/RAM path - that mirrors the coverage
/// available in production too, since GpuHealthMonitor self-gates on
/// OperatingSystem.IsWindows().
/// </summary>
public class TemperatureRollupTests
{
    private sealed class StubSensors : ISensorProvider
    {
        public IReadOnlyList<HardwareSensor> CpuSensors { get; init; } = Array.Empty<HardwareSensor>();
        public IReadOnlyList<HardwareSensor> MemorySensors { get; init; } = Array.Empty<HardwareSensor>();

        public string GetCpuModel() => "Stub CPU";
        public IReadOnlyList<HardwareSensor> GetCpuSensors() => CpuSensors;
        public (bool Healthy, float DistanceToTJMax) GetCpuHealth() => (true, 20f);
        public IReadOnlyList<string> GetGpuModels() => Array.Empty<string>();
        public IReadOnlyList<HardwareSensor> GetGpuSensors() => Array.Empty<HardwareSensor>();
        public IReadOnlyList<GpuReadout> GetGpus() => Array.Empty<GpuReadout>();
        public IReadOnlyList<HardwareSensor> GetMemorySensors() => MemorySensors;
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

    private static HardwareSensor Sensor(string name, string type, float value) => new()
    {
        Id = $"test/{name}",
        Name = name,
        Type = type,
        Value = value,
        Parent = new SensorParent { Id = "test", Name = "test" },
    };

    // SmartHealthMonitor's constructor differs by platform (Windows needs a
    // live LhmComputer, which spawns real hardware init - unsafe to construct
    // in a unit test); off Windows it is parameterless. Mirrors the same
    // conditional construction AddNexusDiagnostics already does at DI time.
    private static SmartHealthMonitor CreateSmartHealthMonitor()
    {
#if WINDOWS
        return new SmartHealthMonitor(new LhmComputer());
#else
        return new SmartHealthMonitor();
#endif
    }

    private static TemperatureRollup CreateRollup(
        StubSensors sensors, InMemoryTemperatureHistoryStore store) =>
        new(sensors, new GpuHealthMonitor(), CreateSmartHealthMonitor(), store);

    [Fact]
    public void Tick_accumulates_cpu_reading_but_does_not_flush_before_the_bucket_rolls_over()
    {
        var sensors = new StubSensors { CpuSensors = new[] { Sensor("Package", "Temperature", 55f) } };
        var store = new InMemoryTemperatureHistoryStore();
        var rollup = CreateRollup(sensors, store);

        rollup.Tick(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Empty(store.Query(0, long.MaxValue));
    }

    [Fact]
    public void Tick_flushes_a_bucket_once_the_next_bucket_starts()
    {
        var sensors = new StubSensors { CpuSensors = new[] { Sensor("Package", "Temperature", 50f) } };
        var store = new InMemoryTemperatureHistoryStore();
        var rollup = CreateRollup(sensors, store);

        rollup.Tick(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        rollup.Tick(new DateTime(2026, 1, 1, 0, 5, 0, DateTimeKind.Utc));

        var row = Assert.Single(store.Query(0, long.MaxValue));
        Assert.Equal("cpu", row.ComponentId);
        Assert.Equal("cpu", row.Kind);
        Assert.Equal(50.0, row.AvgC);
    }

    [Fact]
    public void Tick_reads_ram_temperature_sensors_whose_name_mentions_dimm_or_memory()
    {
        var sensors = new StubSensors
        {
            MemorySensors = new[]
            {
                Sensor("DIMM_A1", "Temperature", 40f),
                Sensor("Memory Controller", "Load", 10f), // wrong type: not a temperature sensor
                Sensor("Fan Speed", "Temperature", 30f),  // wrong name: not dimm/memory
            },
        };
        var store = new InMemoryTemperatureHistoryStore();
        var rollup = CreateRollup(sensors, store);

        rollup.Tick(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        rollup.Tick(new DateTime(2026, 1, 1, 0, 5, 0, DateTimeKind.Utc));

        var row = Assert.Single(store.Query(0, long.MaxValue));
        Assert.Equal("ram:0", row.ComponentId);
        Assert.Equal(40.0, row.AvgC);
    }

    [Fact]
    public void Tick_prunes_rows_older_than_the_retention_window_once_a_day()
    {
        var sensors = new StubSensors { CpuSensors = new[] { Sensor("Package", "Temperature", 50f) } };
        var store = new InMemoryTemperatureHistoryStore();
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        store.UpsertBuckets(new[]
        {
            new TemperatureBucketRow("cpu", "cpu", "CPU", new DateTimeOffset(old).ToUnixTimeMilliseconds(), 40, 45, 1),
        });
        var rollup = CreateRollup(sensors, store);

        rollup.Tick(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Empty(store.Query(0, new DateTimeOffset(old).ToUnixTimeMilliseconds() + 1));
    }

    [Fact]
    public void RunStartupMigration_does_not_throw_when_gpu_health_is_unsupported()
    {
        var sensors = new StubSensors();
        var store = new InMemoryTemperatureHistoryStore();
        var rollup = CreateRollup(sensors, store);

        rollup.RunStartupMigration();

        Assert.Empty(store.Query(0, long.MaxValue));
    }
}
