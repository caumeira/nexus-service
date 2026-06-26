using System;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.LianLi;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.LianLi;

public class LianLiLightingDeviceProviderTests
{
    private readonly LianLiHub _hub = new();
    private readonly InMemoryConfigStore _store = new();
    private readonly LianLiLightingDeviceProvider _provider;

    public LianLiLightingDeviceProviderTests()
    {
        _provider = new LianLiLightingDeviceProvider(_hub, _store, new Nexus.Service.Lighting.Np50IdentifyTracker());
    }

    private void Connect() => _hub.State.IsConnected = true;

    // ── Default fan count ──

    [Fact]
    public void Default_fan_count_is_4_per_port()
    {
        var s = _store.Load().Devices.LianLi;
        Assert.Equal(4, s.Port0Fans);
        Assert.Equal(4, s.Port1Fans);
        Assert.Equal(4, s.Port2Fans);
        Assert.Equal(4, s.Port3Fans);
    }

    // ── GetStructures ──

    [Fact]
    public void GetStructures_returns_empty_when_disconnected()
    {
        Assert.Empty(_provider.GetStructures());
    }

    [Fact]
    public void GetStructures_returns_two_structures_per_active_port_at_default_fan_count()
    {
        Connect();
        // All 4 ports default to 4 fans -> 8 structures (2 per port).
        var structures = _provider.GetStructures();
        Assert.Equal(8, structures.Count);
    }

    [Fact]
    public void GetStructures_skips_port_with_zero_fans()
    {
        Connect();
        _store.Update(s => s.Devices.LianLi.SetFans(1, 0));
        _store.Update(s => s.Devices.LianLi.SetFans(3, 0));
        // Ports 0 and 2 active -> 4 structures.
        var structures = _provider.GetStructures();
        Assert.Equal(4, structures.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void GetStructures_segment_led_count_equals_fans_times_16(int fans)
    {
        Connect();
        _store.Update(s =>
        {
            s.Devices.LianLi.SetFans(0, fans);
            s.Devices.LianLi.SetFans(1, 0);
            s.Devices.LianLi.SetFans(2, 0);
            s.Devices.LianLi.SetFans(3, 0);
        });
        var structures = _provider.GetStructures();
        Assert.Equal(2, structures.Count);
        foreach (var st in structures)
        {
            var seg = Assert.Single(st.Segments);
            Assert.Equal(fans * 16, seg.LedCount);
            Assert.Equal(fans * 16, seg.FrameLedCount);
        }
    }

    [Fact]
    public void GetStructures_segment_is_not_resizable()
    {
        Connect();
        _store.Update(s =>
        {
            s.Devices.LianLi.SetFans(0, 4);
            s.Devices.LianLi.SetFans(1, 0);
            s.Devices.LianLi.SetFans(2, 0);
            s.Devices.LianLi.SetFans(3, 0);
        });
        foreach (var st in _provider.GetStructures())
        {
            Assert.False(Assert.Single(st.Segments).Resizable);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void GetStructures_defaultU_and_defaultV_length_equals_led_count(int fans)
    {
        Connect();
        _store.Update(s =>
        {
            s.Devices.LianLi.SetFans(0, fans);
            s.Devices.LianLi.SetFans(1, 0);
            s.Devices.LianLi.SetFans(2, 0);
            s.Devices.LianLi.SetFans(3, 0);
        });
        var ledCount = fans * 16;
        foreach (var st in _provider.GetStructures())
        {
            var seg = Assert.Single(st.Segments);
            Assert.NotNull(seg.DefaultU);
            Assert.NotNull(seg.DefaultV);
            Assert.Equal(ledCount, seg.DefaultU!.Length);
            Assert.Equal(ledCount, seg.DefaultV!.Length);
        }
    }

    [Fact]
    public void GetStructures_defaultV_centers_at_0_5()
    {
        // For a 1-fan zone, all LEDs form a ring centered at v=0.5.
        Connect();
        _store.Update(s =>
        {
            s.Devices.LianLi.SetFans(0, 1);
            s.Devices.LianLi.SetFans(1, 0);
            s.Devices.LianLi.SetFans(2, 0);
            s.Devices.LianLi.SetFans(3, 0);
        });
        var st = _provider.GetStructures()[0];
        var seg = st.Segments[0];
        // Sum of all v values for 1 fan ring = 0.5*16 (cos-distributed, mean=0.5).
        var sumV = 0.0;
        foreach (var v in seg.DefaultV!)
        {
            sumV += v;
        }
        Assert.Equal(0.5 * 16, sumV, 2);
    }

    [Fact]
    public void GetStructures_defaultU_centers_spread_across_0_to_1_for_4_fans()
    {
        Connect();
        _store.Update(s =>
        {
            s.Devices.LianLi.SetFans(0, 4);
            s.Devices.LianLi.SetFans(1, 0);
            s.Devices.LianLi.SetFans(2, 0);
            s.Devices.LianLi.SetFans(3, 0);
        });
        var st = _provider.GetStructures()[0];
        var seg = st.Segments[0];
        // Mean u for each fan clump should be centered at (f+0.5)/4.
        for (var f = 0; f < 4; f++)
        {
            var sumU = 0.0;
            for (var i = 0; i < 16; i++)
            {
                sumU += seg.DefaultU![f * 16 + i];
            }
            var expectedCenter = (f + 0.5) / 4.0;
            Assert.Equal(expectedCenter, sumU / 16.0, 2);
        }
    }

    [Fact]
    public void GetStructures_default_zone_slice_covers_full_segment()
    {
        Connect();
        _store.Update(s =>
        {
            s.Devices.LianLi.SetFans(0, 2);
            s.Devices.LianLi.SetFans(1, 0);
            s.Devices.LianLi.SetFans(2, 0);
            s.Devices.LianLi.SetFans(3, 0);
        });
        var ledCount = 2 * 16;
        foreach (var st in _provider.GetStructures())
        {
            var zone = Assert.Single(st.DefaultZones);
            var slice = Assert.Single(zone.Slices);
            Assert.Equal(0, slice.Segment);
            Assert.Equal(0, slice.Start);
            Assert.Equal(ledCount, slice.Count);
            Assert.Equal(-1, zone.LegacyZoneIndex);
        }
    }

    [Fact]
    public void GetStructures_inner_and_outer_zone_ids_match_expected_pattern()
    {
        Connect();
        _store.Update(s =>
        {
            s.Devices.LianLi.SetFans(0, 4);
            s.Devices.LianLi.SetFans(1, 0);
            s.Devices.LianLi.SetFans(2, 0);
            s.Devices.LianLi.SetFans(3, 0);
        });
        var structures = _provider.GetStructures();
        Assert.Equal(2, structures.Count);
        Assert.Equal("lianli:port0:inner", structures[0].DeviceId);
        Assert.Equal("lianli:port0:outer", structures[1].DeviceId);
    }
}
