using System;
using System.Collections.Generic;
using Nexus.Service.Models.Devices;

namespace Nexus.Service.Lighting.Mappings;

/// <summary>
/// Synthesizes a portable artifact from a device's resolved layout - the
/// export / publish path. v1 artifacts are per-card: one zone entry carrying
/// the card's zone index (0 for whole-device and contributor cards).
/// </summary>
public static class MappingArtifactFactory
{
    public static MappingArtifact FromResolved(LightingDevice card, ResolvedLedLayout resolved, string name, string? description)
    {
        var zoneIndex = Math.Max(0, card.ZoneIndex ?? 0);
        var zone = new MappingZone
        {
            ZoneIndex = zoneIndex,
            LedCount = card.ZoneResizable ? resolved.LedCount : null,
            AspectRatio = resolved.AspectRatio > 0 ? resolved.AspectRatio : null,
            Groups = CloneGroups(resolved.Groups),
        };
        for (int i = 0; i < resolved.LedCount; i++)
        {
            zone.Leds.Add(new MappingLed
            {
                I = i,
                U = i < resolved.U.Length ? resolved.U[i] : 0f,
                V = i < resolved.V.Length ? resolved.V[i] : 0f,
            });
            if (resolved.Disabled is { } flags && i < flags.Length && flags[i])
                zone.Disabled.Add(i);
        }

        var match = new MappingDeviceMatch
        {
            NameHint = card.Name,
            VendorHint = null,
            ZoneSignature = new List<MappingZoneSignature>
            {
                new() { Type = ZoneTypeToInt(card.ZoneType), DefaultLeds = card.LedCount },
            },
        };
        if (TrySplitUsbKey(card.DeviceKey, out var vid, out var pid))
        {
            match.Vid = vid;
            match.Pid = pid;
        }

        return new MappingArtifact
        {
            SchemaVersion = MappingSchema.Version,
            Name = name,
            Description = description,
            Device = new MappingDeviceInfo { Key = card.DeviceKey, Match = match },
            Zones = new List<MappingZone> { zone },
        };
    }

    private static List<MappingGroup> CloneGroups(List<MappingGroup> groups)
    {
        var clone = new List<MappingGroup>(groups.Count);
        foreach (var g in groups)
        {
            var ranges = new List<MappingLedRange>(g.Ranges.Count);
            foreach (var r in g.Ranges)
                ranges.Add(new MappingLedRange { Start = r.Start, End = r.End });
            clone.Add(new MappingGroup { Name = g.Name, Ranges = ranges });
        }
        return clone;
    }

    private static int ZoneTypeToInt(string? zoneType) => zoneType switch
    {
        "single" => 0,
        "matrix" => 2,
        _ => 1,
    };

    private static bool TrySplitUsbKey(string deviceKey, out string vid, out string pid)
    {
        vid = "";
        pid = "";
        if (!deviceKey.StartsWith("usb:", StringComparison.Ordinal))
            return false;
        var parts = deviceKey.Split(':');
        if (parts.Length < 3 || parts[1].Length != 4 || parts[2].Length != 4)
            return false;
        vid = parts[1];
        pid = parts[2];
        return true;
    }
}
