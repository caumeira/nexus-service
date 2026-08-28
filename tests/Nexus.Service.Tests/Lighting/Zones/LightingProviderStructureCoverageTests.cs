using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Devices;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Peripherals.Hyte.Np50;          // INp50Transport, Np50PortInfo
using Nexus.Service.Peripherals.Hyte.QSeriesCooler;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Lighting.Zones;

/// <summary>
/// ZoneTopology resolves the LED map editor's structure and device-map routes only through
/// <see cref="IDeviceStructureSource"/>, and both answer a provider that skips it with
/// "unknown device" at HTTP 200 - which the editor renders as an empty canvas, not an error.
/// </summary>
public class LightingProviderStructureCoverageTests
{
    [Fact]
    public void Every_lighting_provider_exposes_its_device_structure()
    {
        // The composite fans out to the real providers and the stub stands in
        // for OpenRGB on platforms without it; neither owns an LED space.
        var exempt = new[] { typeof(CompositeLightingDeviceProvider), typeof(StubDeviceProvider) };

        var missing = typeof(QSeriesLightingDeviceProvider).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                && typeof(ILightingDeviceProvider).IsAssignableFrom(t)
                && !exempt.Contains(t)
                && !typeof(IDeviceStructureSource).IsAssignableFrom(t))
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void QSeries_structure_covers_the_whole_led_space_under_the_card_id()
    {
        var provider = ConnectedQSeriesProvider(out var hub);

        var structure = Assert.Single(provider.GetStructures());
        Assert.Equal(hub.DeviceId, structure.DeviceId);

        var segment = Assert.Single(structure.Segments);
        Assert.Equal(QSeriesCoolerHub.LedCount, segment.LedCount);
        Assert.Equal(QSeriesCoolerHub.LedCount, segment.FrameLedCount);
        Assert.False(segment.Resizable);

        var zone = Assert.Single(structure.DefaultZones);
        Assert.Equal(hub.DeviceId, zone.Id);
        var slice = Assert.Single(zone.Slices);
        Assert.Equal(0, slice.Segment);
        Assert.Equal(0, slice.Start);
        Assert.Equal(QSeriesCoolerHub.LedCount, slice.Count);
    }

    [Fact]
    public void QSeries_structure_carries_the_panel_and_logo_positions()
    {
        var provider = ConnectedQSeriesProvider(out _);
        var segment = provider.GetStructures()[0].Segments[0];

        Assert.NotNull(segment.DefaultU);
        Assert.NotNull(segment.DefaultV);
        Assert.Equal(QSeriesCoolerHub.LedCount, segment.DefaultU!.Length);
        Assert.Equal(QSeriesCoolerHub.LedCount, segment.DefaultV!.Length);

        // A linear default puts every LED on one row, collapsing the grid's V spread.
        var panelRows = segment.DefaultV.Take(QSeriesCoolerProtocol.BacklightLedCount).Distinct().Count();
        Assert.Equal(QSeriesCoolerProtocol.BacklightRows, panelRows);

        // The logo diamond sits below the panel.
        var panelBottom = segment.DefaultV.Take(QSeriesCoolerProtocol.BacklightLedCount).Max();
        var logoTop = segment.DefaultV.Skip(QSeriesCoolerProtocol.BacklightLedCount).Min();
        Assert.True(logoTop > panelBottom);
    }

    [Fact]
    public void QSeries_structure_refuses_zone_partitions()
    {
        // GetAll and BuildFrames emit a fixed card list rather than iterating
        // ZoneResolution.Resolve, so a saved partition would back no card while
        // ZoneStateDrop had already dropped the legacy one's prefs and layout.
        var provider = ConnectedQSeriesProvider(out _);
        Assert.False(provider.GetStructures()[0].Partitionable);
    }

    [Fact]
    public void QSeries_reports_no_structure_while_disconnected()
    {
        var hub = new QSeriesCoolerHub(new FakeDiscovery(), _ => new FakeTransport());
        var provider = new QSeriesLightingDeviceProvider(hub, new InMemoryConfigStore(), new Np50IdentifyTracker());
        Assert.Empty(provider.GetStructures());
    }

    private static QSeriesLightingDeviceProvider ConnectedQSeriesProvider(out QSeriesCoolerHub hub)
    {
        var discovery = new FakeDiscovery(new QSeriesCoolerPort
        {
            PortName = "COM_TEST", Serial = "QTEST123", Variant = QSeriesCoolerProtocol.VariantQ60,
        });
        hub = new QSeriesCoolerHub(discovery, _ => new FakeTransport());
        Assert.True(hub.EnsureConnected());
        return new QSeriesLightingDeviceProvider(hub, new InMemoryConfigStore(), new Np50IdentifyTracker());
    }

    private sealed class FakeDiscovery : IQSeriesCoolerPortDiscovery
    {
        private readonly QSeriesCoolerPort[] _ports;
        public FakeDiscovery(params QSeriesCoolerPort[] ports) => _ports = ports;
        public IReadOnlyList<QSeriesCoolerPort> Discover() => _ports;
    }

    private sealed class FakeTransport : INp50Transport
    {
        public bool IsOpen => true;
        public string Serial => "QTEST123";
        public void Write(ReadOnlySpan<byte> data) { }
        public void DiscardInput() { }
        public int Read(Span<byte> buffer, int timeoutMs) => 0;
        public void Dispose() { }
    }

    private sealed class InMemoryConfigStore : IConfigStore
    {
        private readonly NexusSettings _settings = new();
        public string SettingsPath => ":memory:";
        public NexusSettings Load() => _settings;
        public void Update(Action<NexusSettings> mutator) { mutator(_settings); OnChanged?.Invoke(); }
        public void Reload() { }
        public void FlushNow() { }
        public event Action? OnChanged;
    }
}
