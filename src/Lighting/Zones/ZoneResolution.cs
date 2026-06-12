using System;
using System.Collections.Generic;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting.Zones;

/// <summary>
/// Resolves a device's current partition (persisted custom zones or the
/// provider-authored default) into the zones that become cards and engine
/// frames. Custom zones carry "{deviceId}:z{ordinal}" ids and
/// "{DeviceName} - {ZoneName}" names; default zones keep their legacy
/// identities. Every zone also carries a RawName (segment default name or
/// the user-given name) for surfaces that already show the device context.
/// A persisted partition that no longer validates against the live segments
/// (e.g. a fixed count changed across firmware) self-heals by falling back
/// to the default partition.
/// </summary>
public static class ZoneResolution
{
    public static IReadOnlyList<ResolvedZone> Resolve(DeviceStructure structure, NexusSettings settings)
    {
        if (settings.Devices.ZonePartitions.TryGetValue(structure.DeviceId, out var defs)
            && defs is { Count: > 0 })
        {
            var normalized = NormalizeDefs(structure, defs);
            if (ZonePartitionValidator.Validate(structure.Segments, normalized).Ok)
            {
                return BuildCustom(structure, normalized);
            }
        }
        return BuildDefaults(structure);
    }

    /// <summary>
    /// Normalization applied before validation and persistence: names are
    /// trimmed, and a whole-segment slice over a resizable segment tracks the
    /// segment's LIVE count (the stored count goes stale whenever the user
    /// resizes the header afterward; rule 2 already pins such slices to the
    /// whole segment, so substituting the live count is always sound).
    /// </summary>
    public static List<ZoneDef> NormalizeDefs(DeviceStructure structure, IReadOnlyList<ZoneDef> defs)
    {
        var result = new List<ZoneDef>(defs.Count);
        foreach (var def in defs)
        {
            var zone = new ZoneDef { Name = def.Name?.Trim() ?? "" };
            if (def.Slices is not null)
            {
                foreach (var slice in def.Slices)
                {
                    var count = slice.Count;
                    if (slice.Segment >= 0 && slice.Segment < structure.Segments.Count)
                    {
                        var seg = structure.Segments[slice.Segment];
                        if (seg.Resizable && slice.Start == 0)
                        {
                            count = seg.LedCount;
                        }
                    }
                    zone.Slices.Add(new ZoneSlice { Segment = slice.Segment, Start = slice.Start, Count = count });
                }
            }
            result.Add(zone);
        }
        return result;
    }

    private static IReadOnlyList<ResolvedZone> BuildDefaults(DeviceStructure structure)
    {
        var zones = new List<ResolvedZone>(structure.DefaultZones.Count);
        for (int i = 0; i < structure.DefaultZones.Count; i++)
        {
            var def = structure.DefaultZones[i];
            zones.Add(new ResolvedZone
            {
                Id = def.Id,
                Name = def.Name,
                RawName = string.IsNullOrEmpty(def.RawName) ? def.Name : def.RawName,
                DeviceKey = def.DeviceKey,
                Ordinal = i,
                IsDefault = true,
                LegacyZoneIndex = def.LegacyZoneIndex,
                Slices = def.Slices,
                LedCount = SumCounts(def.Slices),
                FrameLedCount = SumFrameCounts(structure, def.Slices),
            });
        }
        return zones;
    }

    private static IReadOnlyList<ResolvedZone> BuildCustom(DeviceStructure structure, List<ZoneDef> defs)
    {
        var zones = new List<ResolvedZone>(defs.Count);
        for (int i = 0; i < defs.Count; i++)
        {
            var def = defs[i];
            zones.Add(new ResolvedZone
            {
                Id = CustomZoneId(structure.DeviceId, i),
                Name = $"{structure.Name} - {def.Name}",
                RawName = def.Name,
                DeviceKey = "",
                Ordinal = i,
                IsDefault = false,
                LegacyZoneIndex = -1,
                Slices = def.Slices,
                LedCount = SumCounts(def.Slices),
                FrameLedCount = SumFrameCounts(structure, def.Slices),
            });
        }
        return zones;
    }

    public static string CustomZoneId(string deviceId, int ordinal) => $"{deviceId}:z{ordinal}";

    /// <summary>
    /// Provider-authored stock positions for a zone: the concatenation of its
    /// slices' segment-default spans in zone-local order, so any partition
    /// shape keeps each zone's true sub-shape. Null when any covered segment
    /// has no authored defaults, a span falls outside them, or the effective
    /// and frame counts diverge (resizable headers) - the resolver's linear
    /// default applies then.
    /// </summary>
    public static (float[]? U, float[]? V) DefaultUv(DeviceStructure structure, ResolvedZone zone)
    {
        if (zone.Slices.Count == 0 || zone.LedCount <= 0 || zone.LedCount != zone.FrameLedCount)
        {
            return (null, null);
        }
        var u = new float[zone.LedCount];
        var v = new float[zone.LedCount];
        var pos = 0;
        foreach (var slice in zone.Slices)
        {
            if (slice.Segment < 0 || slice.Segment >= structure.Segments.Count)
            {
                return (null, null);
            }
            var seg = structure.Segments[slice.Segment];
            if (seg.DefaultU is null || seg.DefaultV is null
                || slice.Start < 0 || slice.Count < 0
                || slice.Start + slice.Count > seg.DefaultU.Length
                || slice.Start + slice.Count > seg.DefaultV.Length
                || pos + slice.Count > u.Length)
            {
                return (null, null);
            }
            Array.Copy(seg.DefaultU, slice.Start, u, pos, slice.Count);
            Array.Copy(seg.DefaultV, slice.Start, v, pos, slice.Count);
            pos += slice.Count;
        }
        return pos == zone.LedCount ? (u, v) : (null, null);
    }

    public static ZoneOverrideContext ContextOf(DeviceStructure structure, ResolvedZone zone)
        => new(structure.DeviceId, zone.Slices);

    /// <summary>
    /// Device-space offset (in hardware-reported counts) of the zone's first
    /// LED. Rule 3 guarantees every zone is one contiguous device-space run,
    /// so offset plus <see cref="ResolvedZone.FrameLedCount"/> fully describes
    /// the zone for frame composition.
    /// </summary>
    public static int FrameOffset(DeviceStructure structure, ResolvedZone zone)
    {
        if (zone.Slices.Count == 0)
        {
            return 0;
        }
        var first = zone.Slices[0];
        var offset = 0;
        for (int i = 0; i < first.Segment && i < structure.Segments.Count; i++)
        {
            offset += structure.Segments[i].FrameLedCount;
        }
        return offset + first.Start;
    }

    /// <summary>The segment a zone wholly covers when it is a single whole-resizable-segment zone (rule 2 shape); negative otherwise.</summary>
    public static int WholeResizableSegment(DeviceStructure structure, ResolvedZone zone)
    {
        if (zone.Slices.Count != 1)
        {
            return -1;
        }
        var slice = zone.Slices[0];
        if (slice.Segment < 0 || slice.Segment >= structure.Segments.Count)
        {
            return -1;
        }
        var seg = structure.Segments[slice.Segment];
        return seg.Resizable && slice.Start == 0 ? seg.Index : -1;
    }

    private static int SumCounts(IReadOnlyList<ZoneSlice> slices)
    {
        var sum = 0;
        foreach (var s in slices)
        {
            sum += s.Count;
        }
        return sum;
    }

    private static int SumFrameCounts(DeviceStructure structure, IReadOnlyList<ZoneSlice> slices)
    {
        var sum = 0;
        foreach (var s in slices)
        {
            if (s.Segment >= 0 && s.Segment < structure.Segments.Count)
            {
                var seg = structure.Segments[s.Segment];
                // Whole-resizable slices size frames from the hardware report;
                // partial slices only exist on fixed segments where the two
                // counts are identical.
                sum += seg.Resizable && s.Start == 0 && s.Count == seg.LedCount
                    ? seg.FrameLedCount
                    : s.Count;
            }
        }
        return sum;
    }
}
