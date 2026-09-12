using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Peripherals.Hyte.MiniHub;
using Nexus.Service.Peripherals.Hyte.SmartHub;
using Nexus.Service.Peripherals.Nzxt;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting.Zones;

/// <summary>
/// SetZoneLedCount on a contributor port/channel writes ZoneLedCounts[id]
/// directly, with no per-sub-zone resize control of its own - so a hand-typed
/// count on a chain-owned id must drop the chain record and its partition the
/// same way OpenRgbLightingDeviceProvider does, or the port keeps reporting
/// products that no longer add up to it.
/// </summary>
public class ZoneLedCountChainGuardTests
{
    private static ZoneDef Link(string name, int start, int count)
    {
        var z = new ZoneDef { Name = name };
        z.Slices.Add(new ZoneSlice { Segment = 0, Start = start, Count = count });
        return z;
    }

    private static void SeedChain(TestableConfigStore store, string portId)
    {
        store.Update(s =>
        {
            s.Devices.ZoneLedCounts[portId] = 76;
            s.Devices.ZonePartitions[portId] = new()
            {
                Link("FR12 Trio", 0, 68),
                Link("Y50 Solo Fan", 68, 8),
            };
            s.Devices.PortChains[ZoneResolution.ChainKey(portId, 0)] = new()
            {
                new ChainEntry { Key = "product:hyte-fr12-trio", LedCount = 68 },
                new ChainEntry { Key = "product:hyte-y50-solo", LedCount = 8 },
            };
        });
    }

    [Fact]
    public void MiniHub_hand_typed_count_drops_the_chain_and_partition()
    {
        using var dir = new TempDir();
        var store = new TestableConfigStore(dir.At("settings.json"));
        const string portId = "minihub:MH01:port3";
        SeedChain(store, portId);

        var provider = new MiniHubLightingDeviceProvider(
            new MiniHubHub(new StubMiniHubPortDiscovery(), _ => null!), store, new Np50IdentifyTracker());
        provider.SetZoneLedCount(portId, 40);

        var settings = store.Load();
        Assert.Equal(40, settings.Devices.ZoneLedCounts[portId]);
        Assert.False(settings.Devices.PortChains.ContainsKey(ZoneResolution.ChainKey(portId, 0)));
        Assert.False(settings.Devices.ZonePartitions.ContainsKey(portId));
    }

    [Fact]
    public void SmartHub_hand_typed_count_drops_the_chain_and_partition()
    {
        using var dir = new TempDir();
        var store = new TestableConfigStore(dir.At("settings.json"));
        const string portId = "smarthub:SH01:port2";
        SeedChain(store, portId);

        var provider = new SmartHubLightingDeviceProvider(
            new SmartHubHub(new StubSmartHubPortDiscovery(), _ => null!), store, new Np50IdentifyTracker());
        provider.SetZoneLedCount(portId, 40);

        var settings = store.Load();
        Assert.Equal(40, settings.Devices.ZoneLedCounts[portId]);
        Assert.False(settings.Devices.PortChains.ContainsKey(ZoneResolution.ChainKey(portId, 0)));
        Assert.False(settings.Devices.ZonePartitions.ContainsKey(portId));
    }

    [Fact]
    public void Kraken_hand_typed_count_drops_the_chain_and_partition()
    {
        using var dir = new TempDir();
        var store = new TestableConfigStore(dir.At("settings.json"));
        var portId = KrakenHub.ZoneIdForChannelIndex(1);
        SeedChain(store, portId);

        var provider = new KrakenLightingDeviceProvider(new KrakenHub(), store, new Np50IdentifyTracker());
        provider.SetZoneLedCount(portId, 40);

        var settings = store.Load();
        Assert.Equal(40, settings.Devices.ZoneLedCounts[portId]);
        Assert.False(settings.Devices.PortChains.ContainsKey(ZoneResolution.ChainKey(portId, 0)));
        Assert.False(settings.Devices.ZonePartitions.ContainsKey(portId));
    }
}
