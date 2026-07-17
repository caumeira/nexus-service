using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Mcp.History;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Sensors;
using Xunit;

namespace Nexus.Service.Tests.Mcp;

/// <summary>
/// AiHistoryRecorder.RecordOnce driven synchronously with fixed inputs, per the
/// workspace convention of extracting a BackgroundService's tick into a testable
/// method rather than racing BackgroundService.StartAsync's async prefix (see
/// CloudProfileSyncServiceTests precedent). Covers the AiIntegration.Enabled
/// idle-skip, the history-unavailable idle-skip, curated sensor selection (CPU
/// package temp/CPU total load/CPU package power, discrete-preferred GPU,
/// fan/pump RPM, coolant temperature sources only), and non-finite sanitizing.
/// </summary>
public sealed class AiHistoryRecorderTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "nexus-ai-recorder-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private TestableConfigStore NewStore() => new(Path.Combine(_tempDir, "settings.json"));

    [Fact]
    public void RecordOnce_when_ai_integration_disabled_does_not_record()
    {
        var store = NewStore();
        var sensors = new FakeSensorProvider
        {
            Cpu = { new HardwareSensor { Id = "cpu/package/temp", Name = "CPU Package", Type = "Temperature", Value = 55, Units = "C" } },
        };
        var history = new FakeAiHistoryStore();
        var recorder = new AiHistoryRecorder(sensors, new FakeFanControlProvider(), store, history);

        recorder.RecordOnce(DateTime.UtcNow);

        Assert.Equal(0, history.RecordSamplesCallCount);
    }

    [Fact]
    public void RecordOnce_when_history_store_unavailable_does_not_record()
    {
        var store = NewStore();
        store.Update(s => s.AiIntegration.Enabled = true);
        var history = new FakeAiHistoryStore { IsAvailable = false };
        var recorder = new AiHistoryRecorder(new FakeSensorProvider(), new FakeFanControlProvider(), store, history);

        recorder.RecordOnce(DateTime.UtcNow);

        Assert.Equal(0, history.RecordSamplesCallCount);
    }

    [Fact]
    public void RecordOnce_records_cpu_temperature_load_and_power_preferring_package_and_total()
    {
        var store = NewStore();
        store.Update(s => s.AiIntegration.Enabled = true);
        var sensors = new FakeSensorProvider
        {
            Cpu =
            {
                new HardwareSensor { Id = "cpu/core0/temp", Name = "Core 0", Type = "Temperature", Value = 60, Units = "C" },
                new HardwareSensor { Id = "cpu/package/temp", Name = "CPU Package", Type = "Temperature", Value = 65, Units = "C" },
                new HardwareSensor { Id = "cpu/total/load", Name = "CPU Total", Type = "Load", Value = 42, Units = "%" },
                new HardwareSensor { Id = "cpu/core0/load", Name = "Core 0", Type = "Load", Value = 10, Units = "%" },
                new HardwareSensor { Id = "cpu/package/power", Name = "CPU Package", Type = "Power", Value = 88, Units = "W" },
            },
        };
        var history = new FakeAiHistoryStore();
        var recorder = new AiHistoryRecorder(sensors, new FakeFanControlProvider(), store, history);

        recorder.RecordOnce(DateTime.UtcNow);

        Assert.Equal(1, history.RecordSamplesCallCount);
        var byId = history.RecordedRows.ToDictionary(r => r.SensorId);
        Assert.Equal(65, byId["cpu/package/temp"].Value);
        Assert.Equal("temperature", byId["cpu/package/temp"].Kind);
        Assert.Equal("C", byId["cpu/package/temp"].Unit);
        Assert.Equal(42, byId["cpu/total/load"].Value);
        Assert.Equal("load", byId["cpu/total/load"].Kind);
        Assert.Equal(88, byId["cpu/package/power"].Value);
        Assert.Equal("power", byId["cpu/package/power"].Kind);
        Assert.DoesNotContain("cpu/core0/temp", byId.Keys);
        Assert.DoesNotContain("cpu/core0/load", byId.Keys);
    }

    [Fact]
    public void RecordOnce_records_fan_and_pump_rpm_and_only_coolant_named_temperature_sources()
    {
        var store = NewStore();
        store.Update(s => s.AiIntegration.Enabled = true);
        var fans = new FakeFanControlProvider
        {
            Channels =
            {
                new FanChannel { Id = "fan-1", Name = "Front Fan", Rpm = 1200, Kind = FanKinds.Fan },
                new FanChannel { Id = "pump-1", Name = "AIO Pump", Rpm = 2400, Kind = FanKinds.Pump },
            },
            Sources =
            {
                new TemperatureSource { Id = "hub:coolant", Name = "Coolant Temp", Category = "Hub", Value = 31.5f },
                new TemperatureSource { Id = "hub:liquid", Name = "Liquid In", Category = "Hub", Value = 29f },
                new TemperatureSource { Id = "cpu-package", Name = "CPU Package", Category = "CPU", Value = 55f },
            },
        };
        var history = new FakeAiHistoryStore();
        var recorder = new AiHistoryRecorder(new FakeSensorProvider(), fans, store, history);

        recorder.RecordOnce(DateTime.UtcNow);

        var byId = history.RecordedRows.ToDictionary(r => r.SensorId);
        Assert.Equal("fan", byId["fan-1"].Kind);
        Assert.Equal(1200, byId["fan-1"].Value);
        Assert.Equal("RPM", byId["fan-1"].Unit);
        Assert.Equal("pump", byId["pump-1"].Kind);
        Assert.Equal(2400, byId["pump-1"].Value);
        Assert.Equal("coolant", byId["hub:coolant"].Kind);
        Assert.Equal(31.5, byId["hub:coolant"].Value, precision: 3);
        Assert.Equal("coolant", byId["hub:liquid"].Kind);
        Assert.DoesNotContain("cpu-package", byId.Keys);
    }

    [Fact]
    public void RecordOnce_sanitizes_non_finite_sensor_values_to_zero()
    {
        var store = NewStore();
        store.Update(s => s.AiIntegration.Enabled = true);
        var sensors = new FakeSensorProvider
        {
            Cpu = { new HardwareSensor { Id = "cpu/package/temp", Name = "CPU Package", Type = "Temperature", Value = float.NaN, Units = "C" } },
        };
        var history = new FakeAiHistoryStore();
        var recorder = new AiHistoryRecorder(sensors, new FakeFanControlProvider(), store, history);

        recorder.RecordOnce(DateTime.UtcNow);

        var row = Assert.Single(history.RecordedRows, r => r.SensorId == "cpu/package/temp");
        Assert.Equal(0, row.Value);
    }

    [Fact]
    public void RecordOnce_prefers_the_discrete_gpu_over_an_integrated_one()
    {
        var store = NewStore();
        store.Update(s => s.AiIntegration.Enabled = true);
        var sensors = new FakeSensorProvider
        {
            Gpus =
            {
                new GpuReadout
                {
                    Id = "gpu:igpu",
                    Name = "Integrated",
                    Integrated = true,
                    Sensors = { new HardwareSensor { Id = "igpu/temp", Name = "Core", Type = "Temperature", Value = 40, Units = "C" } },
                },
                new GpuReadout
                {
                    Id = "gpu:dgpu",
                    Name = "Discrete",
                    Integrated = false,
                    Sensors = { new HardwareSensor { Id = "dgpu/temp", Name = "Core", Type = "Temperature", Value = 70, Units = "C" } },
                },
            },
        };
        var history = new FakeAiHistoryStore();
        var recorder = new AiHistoryRecorder(sensors, new FakeFanControlProvider(), store, history);

        recorder.RecordOnce(DateTime.UtcNow);

        var row = Assert.Single(history.RecordedRows, r => r.Kind == "temperature");
        Assert.Equal("dgpu/temp", row.SensorId);
        Assert.Equal(70, row.Value);
    }

    private sealed class FakeSensorProvider : ISensorProvider
    {
        public List<HardwareSensor> Cpu { get; set; } = new();
        public List<HardwareSensor> Memory { get; set; } = new();
        public List<GpuReadout> Gpus { get; set; } = new();

        public string GetCpuModel() => "Test CPU";
        public IReadOnlyList<HardwareSensor> GetCpuSensors() => Cpu;
        public (bool Healthy, float DistanceToTJMax) GetCpuHealth() => (true, 10f);
        public IReadOnlyList<string> GetGpuModels() => Array.Empty<string>();
        public IReadOnlyList<HardwareSensor> GetGpuSensors() => Gpus.Count > 0 ? Gpus[0].Sensors : Array.Empty<HardwareSensor>();
        public IReadOnlyList<GpuReadout> GetGpus() => Gpus;
        public IReadOnlyList<HardwareSensor> GetMemorySensors() => Memory;
        public string GetMemoryTotalFormatted() => "";
        public string GetRamBrandModel() => "";
        public IReadOnlyDictionary<string, StorageComponent> GetStorageComponents(bool includeSmart = true) => new Dictionary<string, StorageComponent>();
        public IReadOnlyList<string> GetStoragePartitions() => Array.Empty<string>();
        public IReadOnlyList<StorageDriveInfo> GetStorageInfo() => Array.Empty<StorageDriveInfo>();
        public string GetStorageBrandModel() => "";
        public IReadOnlyList<HardwareSensor> GetMotherboardSensors() => Array.Empty<HardwareSensor>();
        public string GetMotherboardModel() => "";
        public SensorExtras GetSensorExtras() => new();
        public string GetOsVersion() => "TestOS";
        public void SetPollingRate(int pollingRate) { }
        public Task ReadyAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeFanControlProvider : IFanControlProvider
    {
        public List<FanChannel> Channels { get; set; } = new();
        public List<TemperatureSource> Sources { get; set; } = new();

        public IReadOnlyList<FanChannel> GetFanChannels() => Channels;
        public IReadOnlyList<TemperatureSource> GetTemperatureSources() => Sources;
        public float? ReadTemperature(string sensorId) => null;
        public int SetFanSpeed(string channelId, int dutyPercent) => dutyPercent;
        public void DriveFanSpeed(string channelId, int dutyPercent) { }
        public void ReleaseFan(string channelId) { }
        public void ReleaseAll() { }
        public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
            IReadOnlyList<string> fanIds, IProgress<FanCalibrationProgress> progress, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
    }

    private sealed class FakeAiHistoryStore : IAiHistoryStore
    {
        public bool IsAvailable { get; set; } = true;
        public List<AiHistorySampleRow> RecordedRows { get; } = new();
        public int RecordSamplesCallCount { get; private set; }

        public void RecordSamples(IReadOnlyList<AiHistorySampleRow> rows, DateTime nowUtc)
        {
            RecordSamplesCallCount++;
            RecordedRows.AddRange(rows);
        }

        public IReadOnlyList<string> KnownSensorIds() => Array.Empty<string>();
        public AiHistorySeriesResult? QuerySensorHistory(string sensorId, long fromUtcMs, long toUtcMs, int maxPoints) => null;
        public IReadOnlyList<AiHistorySensorSummaryRow> Summarize(long fromUtcMs, long toUtcMs) => Array.Empty<AiHistorySensorSummaryRow>();
        public void RecordEvent(AiHistoryEventRow row) { }
        public AiHistoryEventQueryResult QueryEvents(long fromUtcMs, string? type, int limit) => new(Array.Empty<AiHistoryEventRow>(), false);
        public void Dispose() { }
    }
}
