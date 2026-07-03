using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Peripherals.Tryx.Panorama;
using Nexus.Service.Routes;
using Nexus.Service.Sensors;
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
        public IReadOnlyList<string> AvailableMediaIds { get; set; } = Array.Empty<string>();
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
    public void Constructor_loads_overlay_position_font_and_size_from_config_store()
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Tryx.OverlayStats = ["CPU Temperature", "GPU Temperature"];
            s.Tryx.OverlayPosX = [0.03, 0.50];
            s.Tryx.OverlayPosY = [0.10, 0.60];
            s.Tryx.OverlayFont = "roboto-bold";
            s.Tryx.OverlaySize = 120;
        });

        var hub = BuildHub(configStore: store);

        Assert.Equal(new[] { 0.03, 0.50 }, hub.Overlay.PosX);
        Assert.Equal(new[] { 0.10, 0.60 }, hub.Overlay.PosY);
        Assert.Equal("roboto-bold", hub.Overlay.Font);
        Assert.Equal(120, hub.Overlay.Size);
    }

    [Fact]
    public void Constructor_defaults_position_font_and_size_on_a_settings_file_that_predates_them()
    {
        // An empty InMemoryConfigStore mirrors a settings.json written before this
        // feature: no OverlayPosX/PosY/Font/Size keys, only their C# defaults apply.
        var hub = BuildHub(configStore: new InMemoryConfigStore());

        Assert.Empty(hub.Overlay.PosX);
        Assert.Empty(hub.Overlay.PosY);
        Assert.Equal("roboto-regular", hub.Overlay.Font);
        Assert.Equal(100, hub.Overlay.Size);
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
    public void SetOverlay_persists_and_writes_an_rk_overlay_frame()
    {
        var store = new InMemoryConfigStore();
        var recording = new RecordingTransport();
        var hub = BuildHub(
            discovery: new StubDiscovery(),
            transportFactory: _ => recording,
            configStore: store);
        hub.EnsureConnected();
        recording.Writes.Clear();
        var overlay = new TryxOverlayConfig
        {
            Stats = ["CPU Temperature", "GPU Temperature"],
            Color = "#00ff00",
            Align = "Left",
            Filter = "blur",
            Opacity = 80,
            PosX = [0.03, 0.50],
            PosY = [0.10, 0.60],
            Font = "roboto-bold",
            Size = 120,
        };

        var ok = hub.SetOverlay(overlay);

        Assert.True(ok);
        Assert.Equal("#00ff00", store.Load().Tryx.OverlayColor);
        Assert.Equal("Left", store.Load().Tryx.OverlayAlign);
        Assert.Equal(new[] { 0.03, 0.50 }, store.Load().Tryx.OverlayPosX);
        Assert.Equal(new[] { 0.10, 0.60 }, store.Load().Tryx.OverlayPosY);
        Assert.Equal("roboto-bold", store.Load().Tryx.OverlayFont);
        Assert.Equal(120, store.Load().Tryx.OverlaySize);
        Assert.Single(recording.Writes);
    }

    [Fact]
    public void SetOverlay_writes_an_rk_frame_using_the_configured_position_font_and_size()
    {
        var store = new InMemoryConfigStore();
        var recording = new RecordingTransport();
        var hub = BuildHub(
            discovery: new StubDiscovery(),
            transportFactory: _ => recording,
            configStore: store);
        hub.EnsureConnected();
        recording.Writes.Clear();
        var overlay = new TryxOverlayConfig
        {
            Stats = ["CPU Temperature"],
            Color = "#ffffff",
            PosX = [0.03],
            PosY = [0.10],
            Font = "monospace",
            Size = 50,
        };

        hub.SetOverlay(overlay);

        // StubSensors reports all-zero sensors, so "CPU Temperature" maps to "0°C".
        var expected = TryxRkProtocol.BuildOverlay(
            new[] { new TryxOverlayLine("CPU Temperature", "0°C") },
            new[] { (0.03, 0.10) }, colorRgb: 0xFFFFFF, fontName: "monospace", sizePercent: 50);

        Assert.Equal(expected, Assert.Single(recording.Writes));
    }

    [Fact]
    public void SetPreset_writes_the_wallpaper_config_and_persists()
    {
        var store = new InMemoryConfigStore();
        var recording = new RecordingTransport();
        var hub = BuildHub(
            discovery: new StubDiscovery(),
            transportFactory: _ => recording,
            configStore: store);
        hub.EnsureConnected();
        recording.Writes.Clear();

        var media = TryxRkProtocol.PresetMediaFile(3);
        var ok = hub.SetPreset(media);

        Assert.True(ok);
        Assert.Single(recording.Writes);
        Assert.Contains(media, Encoding.UTF8.GetString(recording.Writes[0]));
        Assert.Equal(media, store.Load().Tryx.CurrentMedia);
        Assert.False(store.Load().Tryx.CurrentMediaIsCustom);
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

        hub.SetBrightness(55);

        Assert.Equal(55, store.Load().Tryx.Brightness);
    }

    [Fact]
    public void SetBrightness_writes_rk_brightness_frame()
    {
        var store = new InMemoryConfigStore();
        var recording = new RecordingTransport();
        var hub = BuildHub(
            discovery: new StubDiscovery(),
            transportFactory: _ => recording,
            configStore: store);
        hub.EnsureConnected();
        recording.Writes.Clear();

        hub.SetBrightness(42);

        Assert.Equal(TryxRkProtocol.BuildConfig(true, 42), Assert.Single(recording.Writes));
    }

    [Fact]
    public void EnsureConnected_sends_persisted_brightness_as_rk_frame()
    {
        var store = new InMemoryConfigStore();
        // Empty overlay stats keep ApplyInitialConfig's write to just the brightness
        // frame; the overlay-frame write on connect is covered separately below.
        store.Update(s => { s.Tryx.Brightness = 80; s.Tryx.OverlayStats = []; });

        var recording = new RecordingTransport();
        var hub = BuildHub(
            discovery: new StubDiscovery(),
            transportFactory: _ => recording,
            configStore: store);

        hub.EnsureConnected();

        Assert.Equal(TryxRkProtocol.BuildConfig(true, 80), Assert.Single(recording.Writes));
    }

    [Fact]
    public void EnsureConnected_also_sends_the_overlay_frame_when_stats_are_configured()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Tryx.OverlayStats = ["CPU Temperature"]);

        var recording = new RecordingTransport();
        var hub = BuildHub(
            discovery: new StubDiscovery(),
            transportFactory: _ => recording,
            configStore: store);

        hub.EnsureConnected();

        Assert.Equal(2, recording.Writes.Count);
    }

    // ── Preset availability ──

    [Fact]
    public void AvailableMediaIds_reflects_the_connected_transport()
    {
        var recording = new RecordingTransport { AvailableMediaIds = new[] { "default_01", "default_02" } };
        var hub = BuildHub(discovery: new StubDiscovery(), transportFactory: _ => recording);
        hub.EnsureConnected();

        var presets = TryxRoutes.ResolveAvailablePresets(hub.AvailableMediaIds);

        Assert.Equal(2, presets.Count);
        Assert.Equal("default_01", presets[0].Id);
        Assert.Equal("default_02", presets[1].Id);
    }

    [Fact]
    public void AvailableMediaIds_falls_back_to_the_first_six_before_the_panel_reports_any()
    {
        var recording = new RecordingTransport();
        var hub = BuildHub(discovery: new StubDiscovery(), transportFactory: _ => recording);
        hub.EnsureConnected();

        var presets = TryxRoutes.ResolveAvailablePresets(hub.AvailableMediaIds);

        Assert.Equal(6, presets.Count);
    }

    [Fact]
    public void AvailableMediaIds_is_empty_when_disconnected()
    {
        var hub = BuildHub();

        Assert.Empty(hub.AvailableMediaIds);
    }

    // ── Task 2: tryx.status overlay ──

    [Fact]
    public void Overlay_getter_reflects_the_setter()
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
        Assert.Equal(new[] { "CPU Temperature" }, hub.Overlay.Stats);
    }

    [Fact]
    public void Hub_overlay_reflects_persisted_settings()
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Tryx.OverlayStats = ["GPU Temperature", "CPU Usage"];
            s.Tryx.OverlayColor = "#112233";
            s.Tryx.OverlayAlign = "Right";
        });
        var hub = BuildHub(configStore: store);

        Assert.Equal("#112233", hub.Overlay.Color);
        Assert.Equal("Right", hub.Overlay.Align);
        Assert.Equal(2, hub.Overlay.Stats.Length);
        Assert.Equal("GPU Temperature", hub.Overlay.Stats[0]);
        Assert.Equal("CPU Usage", hub.Overlay.Stats[1]);
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
