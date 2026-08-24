using System;
using System.Collections.Generic;
using Nexus.Service.Lighting.Rgb;
using Xunit;

namespace Nexus.Service.Tests.Lighting.Rgb;

/// <summary>Zone summary arithmetic for the settled-device log.</summary>
public class RgbBridgeZoneSummaryTests
{
    private static RgbDevice Device(params int[] zoneLedCounts)
    {
        var d = new RgbDevice { Index = 0, Name = "board" };
        var total = 0;
        foreach (var c in zoneLedCounts)
        {
            d.Zones.Add(new RgbZone { Name = $"z{d.Zones.Count}", LedCount = c });
            total += c;
        }
        d.LedCount = total;
        return d;
    }

    [Fact]
    public void SumZoneLeds_AddsEveryZone()
    {
        Assert.Equal(325, RgbBridge.SumZoneLeds(Device(60, 80, 80, 15, 10, 80)));
    }

    [Fact]
    public void SumZoneLeds_IsZeroWithoutZones()
    {
        Assert.Equal(0, RgbBridge.SumZoneLeds(new RgbDevice()));
    }

    [Fact]
    public void DescribeZones_ListsEveryZoneCount()
    {
        Assert.Equal(" zones=[60,80,80,15,10,80]", RgbBridge.DescribeZones(Device(60, 80, 80, 15, 10, 80)));
    }

    [Fact]
    public void DescribeZones_OmittedBelowTwoZones()
    {
        Assert.Equal("", RgbBridge.DescribeZones(Device(84)));
        Assert.Equal("", RgbBridge.DescribeZones(new RgbDevice()));
    }

    [Fact]
    public void SignatureTracksZoneCountsWhenTheDeviceTotalDoesNot()
    {
        // A controller can accept a zone resize and leave its device LED count
        // untouched; a signature built from the total alone never re-logs that.
        var before = Device(1, 80);
        var after = Device(60, 80);
        after.LedCount = before.LedCount;

        Assert.NotEqual(
            RgbBridge.BuildDeviceSignature(new[] { before }),
            RgbBridge.BuildDeviceSignature(new[] { after }));
    }

    [Fact]
    public void TryMarkLogged_HonoursACustomCapAndForgetsPastIt()
    {
        var logged = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(RgbBridge.TryMarkLogged(logged, $"k{i}", cap: 3));
        }
        Assert.False(RgbBridge.TryMarkLogged(logged, "k0", cap: 3));

        Assert.True(RgbBridge.TryMarkLogged(logged, "k3", cap: 3));
        Assert.Single(logged);
        Assert.True(RgbBridge.TryMarkLogged(logged, "k0", cap: 3));
    }
}
