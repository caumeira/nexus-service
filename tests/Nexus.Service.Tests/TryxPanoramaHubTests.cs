using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Peripherals.Tryx.Panorama;
using Nexus.Service.Routes;
using Nexus.Service.Sensors;
using Nexus.Service.Widgets;
using Nexus.Service.Widgets.AppActions;
using Xunit;

namespace Nexus.Service.Tests;

public class TryxPanoramaHubTests
{
    // ── Stubs ──

    private sealed class RecordingTransport : ITryxPanoramaTransport
    {
        public bool IsOpen { get; set; } = true;
        public string Serial => "test-serial";
        public string PortName => "COM1";
        public List<byte[]> Writes { get; } = new();
        public void Write(ReadOnlySpan<byte> data) => Writes.Add(data.ToArray());
        public void Dispose() { }
    }

    private sealed class StubDiscovery : ITryxPanoramaPanelDiscovery
    {
        private readonly TryxPanoramaPortInfo _port;
        public StubDiscovery(string portName = "COM1")
        {
            _port = new TryxPanoramaPortInfo { PortName = portName, Serial = "test-serial" };
        }
        public IReadOnlyList<TryxPanoramaPortInfo> Discover() => new[] { _port };
    }

    private sealed class EmptyDiscovery : ITryxPanoramaPanelDiscovery
    {
        public IReadOnlyList<TryxPanoramaPortInfo> Discover() => Array.Empty<TryxPanoramaPortInfo>();
    }

    private sealed class StubSensors : ISensorProvider
    {
        public IReadOnlyList<HardwareSensor> CpuSensors { get; init; } = Array.Empty<HardwareSensor>();
        public IReadOnlyList<HardwareSensor> GpuSensors { get; init; } = Array.Empty<HardwareSensor>();
        public IReadOnlyList<HardwareSensor> MemorySensors { get; init; } = Array.Empty<HardwareSensor>();
        public IReadOnlyList<HardwareSensor> MotherboardSensors { get; init; } = Array.Empty<HardwareSensor>();

        public string GetCpuModel() => "TestCPU";
        public IReadOnlyList<HardwareSensor> GetCpuSensors() => CpuSensors;
        public (bool Healthy, float DistanceToTJMax) GetCpuHealth() => (true, 20f);
        public IReadOnlyList<string> GetGpuModels() => Array.Empty<string>();
        public IReadOnlyList<HardwareSensor> GetGpuSensors() => GpuSensors;
        public IReadOnlyList<GpuReadout> GetGpus()
        {
            if (GpuSensors.Count == 0)
            {
                return Array.Empty<GpuReadout>();
            }
            return new[]
            {
                new GpuReadout
                {
                    Name = "TestGPU",
                    Integrated = false,
                    Sensors = new List<HardwareSensor>(GpuSensors),
                },
            };
        }
        public IReadOnlyList<HardwareSensor> GetMemorySensors() => MemorySensors;
        public string GetMemoryTotalFormatted() => "32 GB";
        public string GetRamBrandModel() => "";
        public IReadOnlyDictionary<string, StorageComponent> GetStorageComponents() =>
            new Dictionary<string, StorageComponent>();
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
        Units = "",
        Formatted = value.ToString(),
        Parent = new SensorParent { Id = "test", Name = "test" },
    };

    private sealed class InMemoryConfigStore : IConfigStore
    {
        private NexusSettings _doc = new();
        public string SettingsPath => "";
        public NexusSettings Load() => _doc;
        public void Update(Action<NexusSettings> mutator) { mutator(_doc); OnChanged?.Invoke(); }
        public void FlushNow() { }
        public void Reload() { }
        public event Action? OnChanged;
    }

    private static TryxPanoramaHub BuildHub(
        ITryxPanoramaPanelDiscovery? discovery = null,
        Func<TryxPanoramaPortInfo, ITryxPanoramaTransport>? transportFactory = null,
        ISensorProvider? sensors = null,
        IConfigStore? configStore = null)
    {
        return new TryxPanoramaHub(
            discovery ?? new EmptyDiscovery(),
            transportFactory ?? (_ => new RecordingTransport()),
            sensors ?? new StubSensors(),
            configStore ?? new InMemoryConfigStore());
    }

    // ── Task 1: Persistence ──

    [Fact]
    public void Constructor_loads_overlay_from_config_store()
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Tryx.OverlayStats = ["GPU Temperature", "CPU Usage"];
            s.Tryx.OverlayColor = "#ff0000";
            s.Tryx.OverlayAlign = "Right";
            s.Tryx.OverlayOpacity = 75;
        });

        var hub = BuildHub(configStore: store);

        Assert.Equal(new[] { "GPU Temperature", "CPU Usage" }, hub.Overlay.Stats);
        Assert.Equal("#ff0000", hub.Overlay.Color);
        Assert.Equal("Right", hub.Overlay.Align);
        Assert.Equal(75, hub.Overlay.Opacity);
    }

    [Fact]
    public void Constructor_loads_current_media_from_config_store()
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Tryx.CurrentMedia = "myclip.mp4";
            s.Tryx.CurrentMediaIsCustom = true;
            s.Tryx.Brightness = 60;
        });

        var hub = BuildHub(configStore: store);

        Assert.Equal("myclip.mp4", hub.State.CurrentMedia);
        Assert.True(hub.State.CurrentMediaIsCustom);
        Assert.Equal(60, hub.State.Brightness);
    }

    [Fact]
    public void SetOverlay_persists_to_config_store()
    {
        var store = new InMemoryConfigStore();
        var hub = BuildHub(configStore: store);
        var overlay = new TryxOverlayConfig
        {
            Stats = ["CPU Temperature", "GPU Temperature"],
            Color = "#00ff00",
            Align = "Left",
            Filter = "blur",
            Opacity = 80,
        };

        hub.SetOverlay(overlay);

        var saved = store.Load().Tryx;
        Assert.Equal(new[] { "CPU Temperature", "GPU Temperature" }, saved.OverlayStats);
        Assert.Equal("#00ff00", saved.OverlayColor);
        Assert.Equal("Left", saved.OverlayAlign);
        Assert.Equal("blur", saved.OverlayFilter);
        Assert.Equal(80, saved.OverlayOpacity);
    }

    [Fact]
    public void SetPreset_persists_media_selection()
    {
        var store = new InMemoryConfigStore();
        var recording = new RecordingTransport();
        var hub = BuildHub(
            discovery: new StubDiscovery(),
            transportFactory: _ => recording,
            configStore: store);
        hub.EnsureConnected();

        hub.SetPreset("Pre-set 3: Quantum time capsule");

        var saved = store.Load().Tryx;
        Assert.Equal("Pre-set 3: Quantum time capsule", saved.CurrentMedia);
        Assert.False(saved.CurrentMediaIsCustom);
    }

    [Fact]
    public void SetBrightness_persists_brightness()
    {
        var store = new InMemoryConfigStore();
        var recording = new RecordingTransport();
        var hub = BuildHub(
            discovery: new StubDiscovery(),
            transportFactory: _ => recording,
            configStore: store);
        hub.EnsureConnected();
        hub.SetPreset("Pre-set 1: Cooling delivery");

        hub.SetBrightness(55);

        Assert.Equal(55, store.Load().Tryx.Brightness);
    }

    [Fact]
    public void EnsureConnected_sends_config_when_current_media_is_persisted()
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Tryx.CurrentMedia = "Pre-set 2: Migration";
            s.Tryx.CurrentMediaIsCustom = false;
            s.Tryx.Brightness = 80;
        });

        var recording = new RecordingTransport();
        var hub = BuildHub(
            discovery: new StubDiscovery(),
            transportFactory: _ => recording,
            configStore: store);

        hub.EnsureConnected();

        Assert.True(recording.Writes.Count > 0);
    }

    [Fact]
    public void EnsureConnected_skips_config_when_no_media_persisted()
    {
        var store = new InMemoryConfigStore();
        var recording = new RecordingTransport();
        var hub = BuildHub(
            discovery: new StubDiscovery(),
            transportFactory: _ => recording,
            configStore: store);

        hub.EnsureConnected();

        Assert.Empty(recording.Writes);
    }

    // ── Task 2: tryx.status overlay ──

    [Fact]
    public void Overlay_getter_reflects_current_in_memory_overlay()
    {
        var hub = BuildHub();
        var ov = new TryxOverlayConfig
        {
            Stats = ["CPU Temperature"],
            Color = "#aabbcc",
            Align = "Center",
        };
        hub.SetOverlay(ov);

        Assert.Equal("#aabbcc", hub.Overlay.Color);
        Assert.Equal("Center", hub.Overlay.Align);
        Assert.Single(hub.Overlay.Stats);
        Assert.Equal("CPU Temperature", hub.Overlay.Stats[0]);
    }

    [Fact]
    public async Task TryxStatus_action_includes_overlay_snapshot()
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Tryx.OverlayStats = ["GPU Temperature", "CPU Usage"];
            s.Tryx.OverlayColor = "#112233";
            s.Tryx.OverlayAlign = "Right";
        });
        var hub = BuildHub(configStore: store);
        var services = new ServiceCollection();
        services.AddSingleton(hub);
        var sp = services.BuildServiceProvider();

        var registry = new AppActionRegistry();
        TryxActions.RegisterAll(registry);
        Assert.True(registry.TryGet("tryx.status", out var handler));
        var result = await handler(sp, null, CancellationToken.None);

        Assert.NotNull(result);
        var doc = JsonDocument.Parse(result!.Value.GetRawText());
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("overlay", out var overlayEl));
        Assert.Equal("#112233", overlayEl.GetProperty("color").GetString());
        Assert.Equal("Right", overlayEl.GetProperty("align").GetString());
        var statsEl = overlayEl.GetProperty("stats");
        Assert.Equal(2, statsEl.GetArrayLength());
        Assert.Equal("GPU Temperature", statsEl[0].GetString());
        Assert.Equal("CPU Usage", statsEl[1].GetString());
    }

    // ── Task 3: Sensor mapping ──

    [Fact]
    public void BuildLiveSensorJson_uses_gpu_temp_fallback_when_no_core_sensor()
    {
        // GPU with a temperature sensor NOT named "Core" - should still be read.
        var gpuSensors = new List<HardwareSensor>
        {
            MakeSensor("GPU Package", "Temperature", 72f),
            MakeSensor("GPU Core", "Load", 55f),
        };
        var sensors = new StubSensors { GpuSensors = gpuSensors };
        var hub = BuildHub(sensors: sensors);

        var json = hub.BuildLiveSensorJsonForTest();
        var doc = JsonDocument.Parse(json);
        Assert.Equal(72, doc.RootElement.GetProperty("gpu").GetProperty("temperature").GetInt32());
    }

    [Fact]
    public void BuildLiveSensorJson_prefers_gpu_core_temp_over_fallback()
    {
        // When "GPU Core" exists it should be preferred over any other Temperature sensor.
        var gpuSensors = new List<HardwareSensor>
        {
            MakeSensor("GPU Package", "Temperature", 80f),
            MakeSensor("GPU Core", "Temperature", 65f),
        };
        var sensors = new StubSensors { GpuSensors = gpuSensors };
        var hub = BuildHub(sensors: sensors);

        var json = hub.BuildLiveSensorJsonForTest();
        var doc = JsonDocument.Parse(json);
        Assert.Equal(65, doc.RootElement.GetProperty("gpu").GetProperty("temperature").GetInt32());
    }

    [Fact]
    public void BuildLiveSensorJson_uses_cpu_memory_clock_for_memory_speed()
    {
        // AMD-style: memory clock lives in CPU Clock sensors named "Memory".
        var cpuSensors = new List<HardwareSensor>
        {
            MakeSensor("Memory", "Clock", 3600f),
            MakeSensor("CPU Total", "Load", 30f),
        };
        var sensors = new StubSensors { CpuSensors = cpuSensors };
        var hub = BuildHub(sensors: sensors);

        var json = hub.BuildLiveSensorJsonForTest();
        var doc = JsonDocument.Parse(json);
        Assert.Equal(3600, doc.RootElement.GetProperty("memory").GetProperty("speed").GetInt32());
    }

    [Fact]
    public void BuildLiveSensorJson_uses_mobo_memory_clock_when_cpu_has_none()
    {
        var moboSensors = new List<HardwareSensor>
        {
            MakeSensor("Memory Clock", "Clock", 2400f),
        };
        var sensors = new StubSensors { MotherboardSensors = moboSensors };
        var hub = BuildHub(sensors: sensors);

        var json = hub.BuildLiveSensorJsonForTest();
        var doc = JsonDocument.Parse(json);
        Assert.Equal(2400, doc.RootElement.GetProperty("memory").GetProperty("speed").GetInt32());
    }

    [Fact]
    public void BuildLiveSensorJson_memory_speed_is_zero_when_no_clock_sensor()
    {
        var hub = BuildHub(sensors: new StubSensors());

        var json = hub.BuildLiveSensorJsonForTest();
        var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("memory").GetProperty("speed").GetInt32());
    }
}
