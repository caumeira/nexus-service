using Nexus.Service.Lighting.Zones;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting.Zones;

/// <summary>
/// <see cref="ZoneResolution.IsFullyUndriven"/> and
/// <see cref="ZoneResolution.IsSegmentFullyUndriven"/> gate frame writers
/// (Corsair, Keeb, LianLi, Slv3) against the LIVE resolved zone ids rather
/// than a provider's default card id, so a custom partition's zone ids still
/// mark a device or segment fully undriven correctly.
/// </summary>
public class ZoneResolutionUndrivenTests
{
    private static ResolvedZone Zone(string id, int segment) => new()
    {
        Id = id,
        Slices = new List<ZoneSlice> { new() { Segment = segment, Start = 0, Count = 1 } },
    };

    [Fact]
    public void IsFullyUndriven_false_when_undriven_list_is_empty()
    {
        var zones = new[] { Zone("corsair:ch1", 0) };
        Assert.False(ZoneResolution.IsFullyUndriven(zones, new List<string>()));
    }

    [Fact]
    public void IsFullyUndriven_true_when_every_zone_id_is_undriven()
    {
        var zones = new[] { Zone("corsair:ch1", 0) };
        Assert.True(ZoneResolution.IsFullyUndriven(zones, new List<string> { "corsair:ch1" }));
    }

    [Fact]
    public void IsFullyUndriven_recognizes_custom_partition_zone_ids()
    {
        // Under a custom partition, a device's default card id (corsair:ch1)
        // never appears as a card - only the resolved custom zone id does.
        var zones = new[] { Zone("corsair:ch1:z0", 0), Zone("corsair:ch1:z1", 1) };

        Assert.False(ZoneResolution.IsFullyUndriven(zones, new List<string> { "corsair:ch1" }));
        Assert.False(ZoneResolution.IsFullyUndriven(zones, new List<string> { "corsair:ch1:z0" }));
        Assert.True(ZoneResolution.IsFullyUndriven(zones, new List<string> { "corsair:ch1:z0", "corsair:ch1:z1" }));
    }

    [Fact]
    public void IsSegmentFullyUndriven_false_when_no_zone_touches_the_segment()
    {
        var zones = new[] { Zone("keeb:SER1:keys", 0) };
        Assert.False(ZoneResolution.IsSegmentFullyUndriven(zones, segment: 1, new List<string> { "keeb:SER1:keys" }));
    }

    [Fact]
    public void IsSegmentFullyUndriven_true_for_default_keeb_partition()
    {
        var zones = new[] { Zone("keeb:SER1:keys", 0), Zone("keeb:SER1:underglow", 1) };
        var undriven = new List<string> { "keeb:SER1:keys" };

        Assert.True(ZoneResolution.IsSegmentFullyUndriven(zones, segment: 0, undriven));
        Assert.False(ZoneResolution.IsSegmentFullyUndriven(zones, segment: 1, undriven));
    }

    [Fact]
    public void IsSegmentFullyUndriven_false_when_a_spanning_zone_is_still_driven()
    {
        // A custom zone spanning both keys and underglow segments; only the
        // spanning zone's own id gates either segment.
        var spanning = new ResolvedZone
        {
            Id = "keeb:SER1:z0",
            Slices = new List<ZoneSlice>
            {
                new() { Segment = 0, Start = 0, Count = 1 },
                new() { Segment = 1, Start = 0, Count = 1 },
            },
        };

        Assert.False(ZoneResolution.IsSegmentFullyUndriven(new[] { spanning }, segment: 0, new List<string>()));
        Assert.True(ZoneResolution.IsSegmentFullyUndriven(new[] { spanning }, segment: 0, new List<string> { "keeb:SER1:z0" }));
        Assert.True(ZoneResolution.IsSegmentFullyUndriven(new[] { spanning }, segment: 1, new List<string> { "keeb:SER1:z0" }));
    }
}
