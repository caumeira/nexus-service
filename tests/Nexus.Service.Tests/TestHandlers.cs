using System;
using System.Collections.Generic;
using Nexus.Service.Devices.Handlers;
using Nexus.Service.Models.Displays;
using Nexus.Service.Panel;
using Nexus.Service.Peripherals.Hyte.Cnvs;
using Nexus.Service.Peripherals.Hyte.MiniHub;
using Nexus.Service.Peripherals.Hyte.QSeriesCooler;
using Nexus.Service.Peripherals.Hyte.Y70Display;
using Nexus.Service.Persistence;
using Nexus.Service.Platform.Displays;

namespace Nexus.Service.Tests;

/// <summary>
/// Builds device handlers backed by stub port-discovery hubs for detection
/// tests. The stub discovery never opens a port, so the hubs report no
/// connection and an empty firmware version - exactly the state the detection
/// tests assume.
/// </summary>
internal static class TestHandlers
{
    public static CnvsHandler Cnvs() => new(new CnvsHub(new StubCnvsPortDiscovery()));

    public static FanHubHandler FanHub() => new(new MiniHubHub(
        new StubMiniHubPortDiscovery(),
        _ => throw new InvalidOperationException("MiniHub transport is not expected in detection tests")));

    public static QSeriesHandler QSeries() => new(new QSeriesCoolerHub(
        new StubQSeriesCoolerPortDiscovery(),
        _ => throw new InvalidOperationException("Q-series transport is not expected in detection tests")));

    public static Y70Handler Y70(DisplayTopologyService? topology = null) => new(
        new Y70DisplayHub(
            new StubY70DisplayPortDiscovery(),
            _ => throw new InvalidOperationException("Y70 transport is not expected in detection tests")),
        topology ?? FakeTopology());

    /// <summary>DisplayTopologyService over a fixed display list, for Y70 monitor-detection tests.</summary>
    internal static DisplayTopologyService FakeTopology(IReadOnlyList<RawDisplayInfo>? displays = null)
        => new(new FakeTopologyProvider(displays ?? new List<RawDisplayInfo>()), new PanelDeviceRegistry(new InertConfigStore()));

    private sealed class FakeTopologyProvider : IDisplayTopologyProvider
    {
        private readonly IReadOnlyList<RawDisplayInfo>? _displays;
        public FakeTopologyProvider(IReadOnlyList<RawDisplayInfo>? displays) => _displays = displays;
        public bool PositionsAvailable => true;
        public IReadOnlyList<RawDisplayInfo>? Enumerate() => _displays;
    }

    /// <summary>In-memory IConfigStore for unit tests - no disk I/O.</summary>
    private sealed class InertConfigStore : IConfigStore
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
