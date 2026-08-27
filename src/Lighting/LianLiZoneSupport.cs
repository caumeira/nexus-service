using System;
using System.Collections.Generic;
using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Peripherals.LianLi;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Channel composition for the Lian Li Uni Hub. The hub drives 4 ports, each a
/// pair of physical channels (inner ring = 2p, outer ring = 2p+1), 16 LEDs per
/// fan per channel. Composition turns those channels into a configurable set of
/// partitionable devices:
///
///   - One device per active port (a port has fans &gt; 0); the device set and
///     LED counts follow the per-port fan counts set on the device page.
///   - Combine on: a device's inner+outer rings are one zone (1-card default).
///   - Combine off: inner and outer are two zones (the legacy 2-card default,
///                  keeping the `:inner` / `:outer` ids so prior edits survive).
///
/// Each device is an ordinary 2-segment structure, so the user can re-partition
/// it with the zone system independent of the composition choice.
/// </summary>
public static class LianLiZoneSupport
{
    public const int InnerSegment = 0;
    public const int OuterSegment = 1;

    // Concentric ring radii in canvas-normalised units: inner ring nested
    // inside the outer ring per fan. u is divided by the fan count so each
    // fan keeps a round ring inside its 1/fans-wide column.
    private const float InnerRadius = 0.24f;
    private const float OuterRadius = 0.42f;

    /// <summary>
    /// The hub's composition. Lian Li never mirrors, so Mirror is forced false
    /// even if a prior build persisted it; only CombineRings carries over,
    /// defaulting to combined.
    /// </summary>
    public static HubCompositionSettings ReadComposition(NexusSettings settings, string hubId)
    {
        settings.Devices.LightingComposition.TryGetValue(hubId, out var c);
        return new HubCompositionSettings { Mirror = false, CombineRings = c?.CombineRings ?? true };
    }

    public static int ClampFans(int fans) => Math.Clamp(fans, 0, LianLiProtocol.MaxFansPerPort);

    /// <summary>Ports with at least one fan, in ascending order.</summary>
    public static List<int> ActivePorts(LianLiSettings fans)
    {
        var ports = new List<int>(LianLiProtocol.PortCount);
        for (var p = 0; p < LianLiProtocol.PortCount; p++)
        {
            if (ClampFans(fans.GetFans(p)) > 0)
            {
                ports.Add(p);
            }
        }
        return ports;
    }

    /// <summary>Every resolved zone id across the hub's current devices (composition + partitions applied).</summary>
    public static List<string> ZoneIds(NexusSettings settings, string hubId)
    {
        var comp = ReadComposition(settings, hubId);
        var ids = new List<string>();
        foreach (var device in Compose(hubId, comp, settings.Devices.LianLi))
        {
            foreach (var zone in ZoneResolution.Resolve(device.Structure, settings))
            {
                ids.Add(zone.Id);
            }
        }
        return ids;
    }

    /// <summary>Every device (structure) id for the hub's current composition.</summary>
    public static List<string> DeviceIds(NexusSettings settings, string hubId)
    {
        var comp = ReadComposition(settings, hubId);
        var ids = new List<string>();
        foreach (var device in Compose(hubId, comp, settings.Devices.LianLi))
        {
            ids.Add(device.Structure.DeviceId);
        }
        return ids;
    }

    /// <summary>The logical devices for the current composition, in display order.</summary>
    public static List<ComposedDevice> Compose(string hubId, HubCompositionSettings comp, LianLiSettings fans)
    {
        var active = ActivePorts(fans);
        var devices = new List<ComposedDevice>();
        if (active.Count == 0)
        {
            return devices;
        }

        foreach (var p in active)
        {
            devices.Add(BuildDevice(
                hubId, $"port{p}", $"Port {p}", ClampFans(fans.GetFans(p)), comp.CombineRings,
                new[] { p * 2 }, new[] { p * 2 + 1 }));
        }
        return devices;
    }

    private static ComposedDevice BuildDevice(
        string hubId, string slug, string label, int fans, bool combine,
        IReadOnlyList<int> innerChannels, IReadOnlyList<int> outerChannels)
    {
        var deviceId = $"{hubId}:{slug}";
        // The two rings carry different per-fan LED counts (8 inner, 12 outer).
        var innerLeds = fans * LianLiProtocol.InnerLedsPerFan;
        var outerLeds = fans * LianLiProtocol.OuterLedsPerFan;
        var (innerU, innerV) = BuildFanRingUV(fans, LianLiProtocol.InnerLedsPerFan, InnerRadius);
        var (outerU, outerV) = BuildFanEdgeBarsUV(fans, LianLiProtocol.OuterLedsPerFan, OuterRadius);

        var structure = new DeviceStructure
        {
            DeviceId = deviceId,
            Name = $"Lian Li - {label}",
            DeviceKey = DeviceKeyComputer.ForFirstParty(LianLiProtocol.VendorId, LianLiProtocol.ProductId, slug),
        };
        structure.Segments.Add(new StructureSegment
        {
            Index = InnerSegment,
            Name = "Inner Ring",
            LedCount = innerLeds,
            FrameLedCount = innerLeds,
            Resizable = false,
            ZoneType = "linear",
            DefaultU = innerU,
            DefaultV = innerV,
        });
        structure.Segments.Add(new StructureSegment
        {
            Index = OuterSegment,
            Name = "Outer Ring",
            LedCount = outerLeds,
            FrameLedCount = outerLeds,
            Resizable = false,
            ZoneType = "linear",
            DefaultU = outerU,
            DefaultV = outerV,
        });

        if (combine)
        {
            structure.DefaultZones.Add(new DefaultZoneDef
            {
                Id = deviceId,
                Name = $"Lian Li - {label}",
                RawName = "All",
                DeviceKey = structure.DeviceKey,
                LegacyZoneIndex = -1,
                Slices =
                {
                    new ZoneSlice { Segment = InnerSegment, Start = 0, Count = innerLeds },
                    new ZoneSlice { Segment = OuterSegment, Start = 0, Count = outerLeds },
                },
            });
        }
        else
        {
            structure.DefaultZones.Add(new DefaultZoneDef
            {
                Id = $"{deviceId}:inner",
                Name = $"Lian Li - {label} Inner Ring",
                RawName = "Inner Ring",
                DeviceKey = DeviceKeyComputer.ForFirstParty(LianLiProtocol.VendorId, LianLiProtocol.ProductId, $"{slug}inner"),
                LegacyZoneIndex = -1,
                Slices = { new ZoneSlice { Segment = InnerSegment, Start = 0, Count = innerLeds } },
            });
            structure.DefaultZones.Add(new DefaultZoneDef
            {
                Id = $"{deviceId}:outer",
                Name = $"Lian Li - {label} Outer Ring",
                RawName = "Outer Ring",
                DeviceKey = DeviceKeyComputer.ForFirstParty(LianLiProtocol.VendorId, LianLiProtocol.ProductId, $"{slug}outer"),
                LegacyZoneIndex = -1,
                Slices = { new ZoneSlice { Segment = OuterSegment, Start = 0, Count = outerLeds } },
            });
        }

        var channels = new IReadOnlyList<int>[] { innerChannels, outerChannels };
        return new ComposedDevice(structure, channels);
    }

    // The outer "ring" is NOT a ring. Camera-verified on fw 1.4 (2026-08-27):
    // it is two vertical edge bars per fan - the first half of the fan's LEDs
    // drive the LEFT bar, the second half the RIGHT bar (outer index 12 lights
    // fan 2's left edge, 18 and 21 its right). Each bar wraps the fan's top AND
    // bottom corners, so a single LED always lights a mirrored pair and top/
    // bottom is not separately addressable - that is physical, not a mapping
    // bug. Modelling these as a circle scattered the LEDs around an ellipse, so
    // a horizontal sweep filled from both edges toward the centre.
    private static (float[] u, float[] v) BuildFanEdgeBarsUV(int fans, int ledsPerFan, float radius)
    {
        var perBar = ledsPerFan / 2;
        var ledCount = fans * ledsPerFan;
        var u = new float[ledCount];
        var v = new float[ledCount];
        for (var f = 0; f < fans; f++)
        {
            var centerU = (f + 0.5f) / fans;
            for (var i = 0; i < ledsPerFan; i++)
            {
                var onRightBar = i >= perBar;
                // u is CONSTANT per bar. The bar is physically an arc (centre ->
                // edge -> centre), but modelling that curve makes the two bars
                // overlap in u, and a horizontal sweep then fills each bar from
                // its edge inward - four bands instead of a clean left-to-right.
                // Hardware-verified 2026-08-27: flat bars read correctly
                // left-to-right, the arc model did not. Keep them flat.
                var alongBar = perBar <= 1 ? 0.5f : (i % perBar) / (float)(perBar - 1);
                u[f * ledsPerFan + i] = centerU + (onRightBar ? 1f : -1f) * (radius / fans);
                v[f * ledsPerFan + i] = 0.5f - radius + 2f * radius * alongBar;
            }
        }
        return (u, v);
    }

    // One ring of ledsPerFan LEDs per fan, fans laid side by side along u;
    // radius/fans in u keeps each ring round inside its column.
    private static (float[] u, float[] v) BuildFanRingUV(int fans, int ledsPerFan, float radius)
    {
        var ledCount = fans * ledsPerFan;
        var u = new float[ledCount];
        var v = new float[ledCount];
        for (var f = 0; f < fans; f++)
        {
            var centerU = (f + 0.5f) / fans;
            for (var i = 0; i < ledsPerFan; i++)
            {
                // Inner ring only - the outer is two bars, see above. Start at
                // pi, not 0: each fan's LED 0 sits at 9 o'clock, so angle 0 would
                // put index 0 at the rightmost u and mirror every horizontal
                // sweep. Confirmed on hardware 2026-08-27 - with angle 0 a
                // left-to-right fade filled each fan right-to-left; with pi it
                // fills correctly.
                var angle = Math.PI + (i / (double)ledsPerFan) * 2.0 * Math.PI;
                u[f * ledsPerFan + i] = centerU + (radius / fans) * (float)Math.Cos(angle);
                v[f * ledsPerFan + i] = 0.5f + radius * (float)Math.Sin(angle);
            }
        }
        return (u, v);
    }
}
