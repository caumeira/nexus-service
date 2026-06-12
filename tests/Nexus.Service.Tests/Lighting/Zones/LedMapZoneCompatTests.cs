using Nexus.Service.Lighting.Zones;
using Nexus.Service.Persistence;
using Nexus.Service.Routes;

namespace Nexus.Service.Tests.Lighting.Zones;

/// <summary>
/// Write-path compat for the per-card led-map POST/DELETE over a custom
/// partition: a card's save replaces only the device overrides its zone
/// covers (zone-local indices re-keyed through the slices), leaving sibling
/// zones' entries untouched.
/// </summary>
public class LedMapZoneCompatTests
{
    private const string DeviceId = "dev-1";

    private static ZoneOverrideContext SpanContext() => new(DeviceId, new List<ZoneSlice>
    {
        new() { Segment = 0, Start = 6, Count = 4 },
        new() { Segment = 1, Start = 0, Count = 2 },
    });

    [Fact]
    public void Save_replaces_only_the_zone_covered_entries()
    {
        var settings = new NexusSettings();
        settings.Devices.DeviceLedOverrides[DeviceId] = new()
        {
            // Covered by the span zone (will be replaced).
            new SegmentLedOverride { Segment = 0, LedIndex = 7, U = 0.1f, V = 0.1f },
            new SegmentLedOverride { Segment = 1, LedIndex = 1, U = 0.2f, V = 0.2f },
            // Outside the span zone (must survive).
            new SegmentLedOverride { Segment = 0, LedIndex = 2, U = 0.3f, V = 0.3f },
            new SegmentLedOverride { Segment = 1, LedIndex = 4, U = 0.4f, V = 0.4f },
        };
        var ctx = SpanContext();

        // Mimics the POST handler: keep outside entries, re-add the body's
        // zone-local overrides mapped to segment space.
        var next = DevicesRoutes.CollectOverridesOutsideZone(settings, ctx);
        Assert.Equal(2, next.Count);
        var body = new List<LedPositionOverride>
        {
            new() { LedIndex = 0, U = 0.9f, V = 0.9f },
            new() { LedIndex = 5, U = 0.8f, V = 0.8f, Disabled = true },
        };
        foreach (var o in body)
        {
            Assert.True(ctx.TryMapToSegment(o.LedIndex, out var segment, out var local));
            next.Add(new SegmentLedOverride
            { Segment = segment, LedIndex = local, U = o.U, V = o.V, Disabled = o.Disabled });
        }
        settings.Devices.DeviceLedOverrides[DeviceId] = next;

        var stored = settings.Devices.DeviceLedOverrides[DeviceId];
        Assert.Equal(4, stored.Count);
        // Outside entries intact.
        Assert.Contains(stored, o => o is { Segment: 0, LedIndex: 2, U: 0.3f });
        Assert.Contains(stored, o => o is { Segment: 1, LedIndex: 4, U: 0.4f });
        // Zone-local 0 = (0, 6); zone-local 5 = (1, 1).
        Assert.Contains(stored, o => o is { Segment: 0, LedIndex: 6, U: 0.9f });
        Assert.Contains(stored, o => o is { Segment: 1, LedIndex: 1, U: 0.8f, Disabled: true });
        // The previously stored covered entries are gone.
        Assert.DoesNotContain(stored, o => o is { Segment: 0, LedIndex: 7 });
    }

    [Fact]
    public void Identity_context_keeps_other_devices_untouched()
    {
        var settings = new NexusSettings();
        settings.Devices.DeviceLedOverrides["card-a"] = new()
        {
            new SegmentLedOverride { Segment = 0, LedIndex = 1, U = 0.5f, V = 0.5f },
        };
        var ctx = ZoneOverrideContext.Identity("card-b");
        var outside = DevicesRoutes.CollectOverridesOutsideZone(settings, ctx);
        Assert.Empty(outside);
        Assert.Single(settings.Devices.DeviceLedOverrides["card-a"]);
    }
}
