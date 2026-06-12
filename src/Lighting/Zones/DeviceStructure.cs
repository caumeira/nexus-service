using System;
using System.Collections.Generic;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting.Zones;

/// <summary>
/// Hardware-reported subdivision of a device's LED space. Resizable means the
/// protocol states the LED count is user-wired and can change (motherboard
/// ARGB headers, 12V channels), which would shift concatenated indices;
/// fixed counts never move. Flags are authored for first-party devices and
/// derived from controller data for OpenRGB. No physical inference anywhere.
/// </summary>
public sealed class StructureSegment
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Effective LED count shown to the user; for resizable segments the persisted resize choice wins over the hardware report.</summary>
    public int LedCount { get; set; }
    /// <summary>Hardware-reported count used for engine frame sizing and device-space offsets. Equals <see cref="LedCount"/> for fixed segments.</summary>
    public int FrameLedCount { get; set; }
    public bool Resizable { get; set; }
    /// <summary>"single" | "linear" | "matrix" - same vocabulary as the card DTO.</summary>
    public string ZoneType { get; set; } = "linear";
}

/// <summary>
/// Provider-authored zone of the DEFAULT partition. Default zones keep the
/// LEGACY card ids, names, and device keys so existing prefs, layouts, and
/// applied mappings keep working untouched when no custom partition exists.
/// </summary>
public sealed class DefaultZoneDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string DeviceKey { get; set; } = "";
    /// <summary>OpenRGB zone index the legacy card carried (drives the legacy resolution path); negative for whole-device and non-OpenRGB zones.</summary>
    public int LegacyZoneIndex { get; set; } = -1;
    public List<ZoneSlice> Slices { get; set; } = new();
}

/// <summary>
/// One partitionable device as exposed by its provider: a stable identity,
/// the hardware segments of its LED space, and the authored default
/// partition. Hub ports, smart lights, and other non-partitionable cards
/// never appear here.
/// </summary>
public sealed class DeviceStructure
{
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string DeviceKey { get; set; } = "";
    public List<StructureSegment> Segments { get; set; } = new();
    public List<DefaultZoneDef> DefaultZones { get; set; } = new();
}

/// <summary>Providers with partitionable devices expose their structures through this; <see cref="ZoneTopology"/> aggregates all sources.</summary>
public interface IDeviceStructureSource
{
    /// <summary>Structures for the source's currently-connected partitionable devices.</summary>
    IReadOnlyList<DeviceStructure> GetStructures();
}

/// <summary>A zone resolved against the current partition (default or custom) of one device.</summary>
public sealed class ResolvedZone
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Cross-install fingerprint. Default zones keep the legacy card key; custom zones carry an empty key (community features hidden until device-scope artifacts land).</summary>
    public string DeviceKey { get; set; } = "";
    public int Ordinal { get; set; }
    public bool IsDefault { get; set; }
    /// <summary>See <see cref="DefaultZoneDef.LegacyZoneIndex"/>; always negative for custom zones.</summary>
    public int LegacyZoneIndex { get; set; } = -1;
    /// <summary>Ordered slices in effective (user-facing) counts.</summary>
    public IReadOnlyList<ZoneSlice> Slices { get; set; } = Array.Empty<ZoneSlice>();
    /// <summary>Sum of effective slice counts (card LED count for custom zones).</summary>
    public int LedCount { get; set; }
    /// <summary>Sum of hardware-reported slice counts (engine frame size). Equals <see cref="LedCount"/> unless a resizable segment ignores resize on the wire.</summary>
    public int FrameLedCount { get; set; }
}

/// <summary>
/// Maps a card's zone-local LED indices into the owning device's stable
/// (segment, localIndex) override space. A null slice list is the identity
/// context used for non-partitionable cards: the card is its own
/// single-segment device.
/// </summary>
public sealed class ZoneOverrideContext
{
    public ZoneOverrideContext(string deviceId, IReadOnlyList<ZoneSlice>? slices)
    {
        DeviceId = deviceId;
        Slices = slices;
    }

    public static ZoneOverrideContext Identity(string deviceId) => new(deviceId, null);

    public string DeviceId { get; }
    public IReadOnlyList<ZoneSlice>? Slices { get; }

    /// <summary>Zone-local index for a (segment, localIndex) pair, or negative when the LED is outside this zone.</summary>
    public int MapFromSegment(int segment, int ledIndex)
    {
        if (Slices is null)
        {
            return segment == 0 ? ledIndex : -1;
        }
        var acc = 0;
        foreach (var slice in Slices)
        {
            if (slice.Segment == segment && ledIndex >= slice.Start && ledIndex < slice.Start + slice.Count)
            {
                return acc + (ledIndex - slice.Start);
            }
            acc += slice.Count;
        }
        return -1;
    }

    /// <summary>(segment, localIndex) for a zone-local index; false when out of range.</summary>
    public bool TryMapToSegment(int zoneLocal, out int segment, out int localIndex)
    {
        if (zoneLocal >= 0)
        {
            if (Slices is null)
            {
                segment = 0;
                localIndex = zoneLocal;
                return true;
            }
            var acc = 0;
            foreach (var slice in Slices)
            {
                if (zoneLocal < acc + slice.Count)
                {
                    segment = slice.Segment;
                    localIndex = slice.Start + (zoneLocal - acc);
                    return true;
                }
                acc += slice.Count;
            }
        }
        segment = -1;
        localIndex = -1;
        return false;
    }
}
