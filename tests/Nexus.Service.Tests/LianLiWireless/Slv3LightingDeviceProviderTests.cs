using System;
using System.Linq;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.LianLiWireless;
using Xunit;

namespace Nexus.Service.Tests.LianLiWireless;

public class Slv3LightingDeviceProviderTests
{
    private static readonly byte[] FanMac = Convert.FromHexString("112233445566");

    [Fact]
    public void GetStructures_returns_empty_when_disconnected()
    {
        var hub = new Slv3Hub(new Slv3TestHub.FakeDiscovery(), _ => throw new InvalidOperationException());
        var provider = new Slv3LightingDeviceProvider(hub, new InMemoryConfigStore(), new Np50IdentifyTracker());
        Assert.Empty(provider.GetStructures());
    }

    [Fact]
    public void Bound_fan_chain_becomes_one_device_with_inner_outer_segments()
    {
        var (hub, net, _) = Slv3TestHub.CreateConnected();
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, FanCount = 3 });
        Assert.True(hub.DriveTick());

        var provider = new Slv3LightingDeviceProvider(hub, new InMemoryConfigStore(), new Np50IdentifyTracker());
        var structures = provider.GetStructures();

        var structure = Assert.Single(structures);
        Assert.Equal($"lianli-wireless:{Convert.ToHexString(FanMac)}", structure.DeviceId);
        Assert.Equal(2, structure.Segments.Count);
        // 3 fans * 20 LEDs/ring (half of the 40-LED-per-fan wire layout).
        Assert.Equal(60, structure.Segments[Slv3LightingDeviceProvider.InnerSegment].LedCount);
        Assert.Equal(60, structure.Segments[Slv3LightingDeviceProvider.OuterSegment].LedCount);
        Assert.Equal(2, structure.DefaultZones.Count);
    }

    [Fact]
    public void Unbound_fan_is_not_a_device()
    {
        var (hub, net, _) = Slv3TestHub.CreateConnected();
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());

        var provider = new Slv3LightingDeviceProvider(hub, new InMemoryConfigStore(), new Np50IdentifyTracker());
        Assert.Empty(provider.GetStructures());
    }

    [Fact]
    public void GetAll_emits_a_card_per_zone_with_fan_icon()
    {
        var (hub, net, _) = Slv3TestHub.CreateConnected();
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, FanCount = 1 });
        Assert.True(hub.DriveTick());

        var provider = new Slv3LightingDeviceProvider(hub, new InMemoryConfigStore(), new Np50IdentifyTracker());
        var resp = provider.GetAll();

        Assert.True(resp.IsInit);
        Assert.Equal(2, resp.Devices.Count);
        Assert.All(resp.Devices, d => Assert.Equal("fan", d.IconType));
        Assert.Contains(resp.Devices, d => d.Id.EndsWith(":inner", StringComparison.Ordinal));
        Assert.Contains(resp.Devices, d => d.Id.EndsWith(":outer", StringComparison.Ordinal));
    }

    [Fact]
    public void DeviceId_and_mac_roundtrip()
    {
        var macHex = Convert.ToHexString(FanMac);
        var deviceId = Slv3LightingDeviceProvider.DeviceIdFor(macHex);
        Assert.Equal(macHex, Slv3LightingDeviceProvider.MacFromDeviceId(deviceId));
        Assert.Equal("", Slv3LightingDeviceProvider.MacFromDeviceId("lianli:port0"));
    }

    [Fact]
    public void BuildFrames_reuses_the_same_frame_instance_across_calls()
    {
        var (hub, net, _) = Slv3TestHub.CreateConnected();
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, FanCount = 2 });
        Assert.True(hub.DriveTick());

        var provider = new Slv3LightingDeviceProvider(hub, new InMemoryConfigStore(), new Np50IdentifyTracker());
        var first = provider.BuildFrames(0);
        var second = provider.BuildFrames(0);

        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Same(first[i], second[i]);
        }
    }
}
