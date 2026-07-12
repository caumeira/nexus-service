using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests.Lighting.Rgb;

/// <summary>
/// <see cref="RgbBridge.ComputeFullyUndrivenPhysicals"/> must ignore
/// contributor frames when deciding whether a physical OpenRGB device is
/// fully undriven. Contributor frames (NP50, Keeb, hubs) default their
/// PhysicalIndex to their engine ordinal, which can collide with a real
/// OpenRGB device index once first-party-owned devices are excluded from
/// seeding.
/// </summary>
public class RgbBridgeUndrivenAggregationTests
{
    [Fact]
    public void Contributor_frame_sharing_a_physical_index_does_not_veto_an_undriven_openrgb_device()
    {
        // A bridge frame at PhysicalIndex 1 (as if device index 0 were
        // excluded by first-party ownership, so this is the only OpenRGB
        // frame built) and a driven contributor frame that happens to reuse
        // engine ordinal 1 as its own PhysicalIndex.
        var bridgeFrame = new DeviceFrame(index: 0, id: "openrgb-l-COM5", ledCount: 4, physicalIndex: 1);
        var contributorFrame = new DeviceFrame(index: 1, id: "fake:driven", ledCount: 2);
        Assert.Equal(1, contributorFrame.PhysicalIndex);

        var deviceFrames = new[] { bridgeFrame, contributorFrame };
        var bridgeFrameIds = new HashSet<string> { "openrgb-l-COM5" };
        var undriven = new List<string> { "openrgb-l-COM5" };
        var result = new Dictionary<int, bool>();

        RgbBridge.ComputeFullyUndrivenPhysicals(deviceFrames, bridgeFrameIds, undriven, result);

        Assert.True(result[1]);
    }

    [Fact]
    public void Undriven_contributor_frame_does_not_mark_an_unrelated_openrgb_physical_index()
    {
        var bridgeFrame = new DeviceFrame(index: 0, id: "openrgb-l-COM5", ledCount: 4, physicalIndex: 1);
        var contributorFrame = new DeviceFrame(index: 1, id: "fake:undriven", ledCount: 2);

        var deviceFrames = new[] { bridgeFrame, contributorFrame };
        var bridgeFrameIds = new HashSet<string> { "openrgb-l-COM5" };
        var undriven = new List<string> { "fake:undriven" };
        var result = new Dictionary<int, bool>();

        RgbBridge.ComputeFullyUndrivenPhysicals(deviceFrames, bridgeFrameIds, undriven, result);

        Assert.False(result[1]);
    }

    [Fact]
    public void Mixed_zones_of_the_same_bridge_device_are_not_fully_undriven()
    {
        var zoneA = new DeviceFrame(index: 0, id: "openrgb-s-MB01-0", ledCount: 4, physicalIndex: 0);
        var zoneB = new DeviceFrame(index: 1, id: "openrgb-s-MB01-1", ledCount: 4, physicalIndex: 0);

        var deviceFrames = new[] { zoneA, zoneB };
        var bridgeFrameIds = new HashSet<string> { "openrgb-s-MB01-0", "openrgb-s-MB01-1" };
        var undriven = new List<string> { "openrgb-s-MB01-0" };
        var result = new Dictionary<int, bool>();

        RgbBridge.ComputeFullyUndrivenPhysicals(deviceFrames, bridgeFrameIds, undriven, result);

        Assert.False(result[0]);
    }

    [Fact]
    public void Result_is_cleared_and_left_empty_when_nothing_is_undriven()
    {
        var bridgeFrame = new DeviceFrame(index: 0, id: "openrgb-l-COM5", ledCount: 4, physicalIndex: 1);
        var result = new Dictionary<int, bool> { [99] = true };

        RgbBridge.ComputeFullyUndrivenPhysicals(new[] { bridgeFrame }, new HashSet<string> { "openrgb-l-COM5" }, new List<string>(), result);

        Assert.Empty(result);
    }
}
