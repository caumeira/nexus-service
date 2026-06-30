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
        var leds = fans * LianLiProtocol.LedsPerFanPerChannel;
        var (innerU, innerV) = BuildFanRingUV(fans, InnerRadius);
        var (outerU, outerV) = BuildFanRingUV(fans, OuterRadius);

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
            LedCount = leds,
            FrameLedCount = leds,
            Resizable = false,
            ZoneType = "linear",
            DefaultU = innerU,
            DefaultV = innerV,
        });
        structure.Segments.Add(new StructureSegment
        {
            Index = OuterSegment,
            Name = "Outer Ring",
            LedCount = leds,
            FrameLedCount = leds,
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
                    new ZoneSlice { Segment = InnerSegment, Start = 0, Count = leds },
                    new ZoneSlice { Segment = OuterSegment, Start = 0, Count = leds },
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
                Slices = { new ZoneSlice { Segment = InnerSegment, Start = 0, Count = leds } },
            });
            structure.DefaultZones.Add(new DefaultZoneDef
            {
                Id = $"{deviceId}:outer",
                Name = $"Lian Li - {label} Outer Ring",
                RawName = "Outer Ring",
                DeviceKey = DeviceKeyComputer.ForFirstParty(LianLiProtocol.VendorId, LianLiProtocol.ProductId, $"{slug}outer"),
                LegacyZoneIndex = -1,
                Slices = { new ZoneSlice { Segment = OuterSegment, Start = 0, Count = leds } },
            });
        }

        var channels = new IReadOnlyList<int>[] { innerChannels, outerChannels };
        return new ComposedDevice(structure, channels);
    }

    // One ring of LedsPerFanPerChannel LEDs per fan, fans laid side by side
    // along u; radius/fans in u keeps each ring round inside its column.
    private static (float[] u, float[] v) BuildFanRingUV(int fans, float radius)
    {
        var ledCount = fans * LianLiProtocol.LedsPerFanPerChannel;
        var u = new float[ledCount];
        var v = new float[ledCount];
        for (var f = 0; f < fans; f++)
        {
            var centerU = (f + 0.5f) / fans;
            for (var i = 0; i < LianLiProtocol.LedsPerFanPerChannel; i++)
            {
                var angle = (i / (double)LianLiProtocol.LedsPerFanPerChannel) * 2.0 * Math.PI;
                u[f * LianLiProtocol.LedsPerFanPerChannel + i] = centerU + (radius / fans) * (float)Math.Cos(angle);
                v[f * LianLiProtocol.LedsPerFanPerChannel + i] = 0.5f + radius * (float)Math.Sin(angle);
            }
        }
        return (u, v);
    }
}
