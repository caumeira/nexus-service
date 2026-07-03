using System;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.LianLiWireless;
using Xunit;

namespace Nexus.Service.Tests.LianLiWireless;

public class Slv3CoolingProviderTests
{
    private const string Mac = "112233445566";

    [Fact]
    public void IsSlv3Id_matches_only_the_wireless_fan_channel_prefix()
    {
        Assert.True(Slv3CoolingProvider.IsSlv3Id($"lianli-wireless:{Mac}:port0"));
        Assert.False(Slv3CoolingProvider.IsSlv3Id("lianli:port0"));
        Assert.False(Slv3CoolingProvider.IsSlv3Id(""));
    }

    [Fact]
    public void GetFanChannels_and_GetAll_empty_when_hub_disconnected()
    {
        var hub = new Slv3Hub(new Slv3TestHub.FakeDiscovery(), _ => throw new InvalidOperationException());
        var provider = new Slv3CoolingProvider(hub, new InMemoryConfigStore());

        Assert.Empty(provider.GetFanChannels());
        Assert.Empty(provider.GetAll());
    }

    [Fact]
    public void GetFanChannels_surfaces_one_channel_per_occupied_port_with_telemetry_driven_mode()
    {
        var (hub, _, _) = Slv3TestHub.CreateConnected();
        hub.State.Fans = new[]
        {
            new Slv3FanInfo
            {
                Mac = Mac,
                BoundToUs = true,
                FanCount = 2,
                Pwm = new[] { Slv3Protocol.PwmFollowMotherboard, 40, 0, 0 },
                Rpm = new[] { 0, 1200, 0, 0 },
            },
        };
        var provider = new Slv3CoolingProvider(hub, new InMemoryConfigStore());

        var channels = provider.GetFanChannels();

        Assert.Equal(2, channels.Count);
        Assert.Equal($"lianli-wireless:{Mac}:port0", channels[0].Id);
        Assert.Equal(FanModes.Auto, channels[0].Mode);       // wire byte 6 -> mobo-sync
        Assert.Equal(Slv3Protocol.PwmFollowMotherboard, channels[0].DutyPercent);
        Assert.Equal($"lianli-wireless:{Mac}:port1", channels[1].Id);
        Assert.Equal(FanModes.Manual, channels[1].Mode);
        Assert.Equal(40, channels[1].DutyPercent);
        Assert.Equal(1200, channels[1].Rpm);
        Assert.Equal(Slv3Protocol.MinDutyPercent, channels[1].MinDuty);
        Assert.All(channels, c => Assert.Equal($"lianli-wireless:{Mac}", c.DeviceId));
    }

    [Fact]
    public void GetFanChannels_skips_unbound_chains_and_zero_fan_count_chains()
    {
        var (hub, _, _) = Slv3TestHub.CreateConnected();
        hub.State.Fans = new[]
        {
            new Slv3FanInfo { Mac = Mac, BoundToUs = false, FanCount = 2 },
            new Slv3FanInfo { Mac = "AABBCCDDEEFF", BoundToUs = true, FanCount = 0 },
        };
        var provider = new Slv3CoolingProvider(hub, new InMemoryConfigStore());

        Assert.Empty(provider.GetFanChannels());
        Assert.Empty(provider.GetAll());
    }

    [Fact]
    public void GetAll_groups_ports_under_one_component_per_chain()
    {
        var (hub, _, _) = Slv3TestHub.CreateConnected();
        hub.State.Fans = new[]
        {
            new Slv3FanInfo
            {
                Mac = Mac,
                BoundToUs = true,
                FanCount = 3,
                Pwm = new[] { 50, Slv3Protocol.PwmFollowMotherboard, Slv3Protocol.PwmFollowMotherboard, 0 },
                Rpm = new[] { 900, 0, 0, 0 },
            },
        };
        var provider = new Slv3CoolingProvider(hub, new InMemoryConfigStore());

        var component = Assert.Single(provider.GetAll());
        Assert.Equal($"lianli-wireless:{Mac}", component.Id);
        Assert.Equal(3, component.Devices.Count);
        Assert.Equal(900, component.Devices[0].Rpm);
        Assert.Equal(50, component.Devices[0].Pwm);
    }

    [Fact]
    public void SetFanSpeed_and_ReleaseFan_drive_the_hub_by_mac_and_port()
    {
        var (hub, _, _) = Slv3TestHub.CreateConnected();
        hub.State.Fans = new[] { new Slv3FanInfo { Mac = Mac, BoundToUs = true, FanCount = 2 } };
        var provider = new Slv3CoolingProvider(hub, new InMemoryConfigStore());

        var applied = provider.SetFanSpeed($"lianli-wireless:{Mac}:port0", 45);

        Assert.Equal(45, applied);
        Assert.Equal(45, hub.GetPortDuty(Mac, 0));

        provider.ReleaseFan($"lianli-wireless:{Mac}:port0");

        Assert.Null(hub.GetPortDuty(Mac, 0));
    }

    [Fact]
    public void ReleaseAll_clears_every_bound_port_back_to_mobo_sync()
    {
        var (hub, _, _) = Slv3TestHub.CreateConnected();
        hub.State.Fans = new[] { new Slv3FanInfo { Mac = Mac, BoundToUs = true, FanCount = 2 } };
        var provider = new Slv3CoolingProvider(hub, new InMemoryConfigStore());
        provider.SetFanSpeed($"lianli-wireless:{Mac}:port0", 60);
        provider.SetFanSpeed($"lianli-wireless:{Mac}:port1", 70);

        provider.ReleaseAll();

        Assert.Null(hub.GetPortDuty(Mac, 0));
        Assert.Null(hub.GetPortDuty(Mac, 1));
    }

    [Fact]
    public void OnHubStateUpdated_restores_a_persisted_manual_duty_once_per_mac()
    {
        var (hub, _, _) = Slv3TestHub.CreateConnected();
        hub.State.Fans = new[] { new Slv3FanInfo { Mac = Mac, BoundToUs = true, FanCount = 2 } };
        var store = new InMemoryConfigStore();
        store.Update(s => s.Cooling.ManualSpeeds[$"lianli-wireless:{Mac}:port1"] = 33);
        var provider = new Slv3CoolingProvider(hub, store);

        provider.OnHubStateUpdated();

        Assert.Null(hub.GetPortDuty(Mac, 0));    // no saved entry: stays mobo-sync
        Assert.Equal(33, hub.GetPortDuty(Mac, 1));

        // A live edit after the initial restore must survive a later tick -
        // OnHubStateUpdated only restores once per MAC per run.
        Assert.True(hub.SetPortDuty(Mac, 1, 80));
        provider.OnHubStateUpdated();
        Assert.Equal(80, hub.GetPortDuty(Mac, 1));
    }
}
