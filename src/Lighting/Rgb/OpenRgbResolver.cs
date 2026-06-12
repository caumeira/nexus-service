using System;
using System.Collections.Generic;

namespace Nexus.Service.Lighting.Rgb;

/// <summary>
/// Resolves a lighting-device id to an OpenRGB device + optional zone index.
/// Extracted from the routes partial so the mapping pipeline can resolve
/// outside an HTTP request. Handles three id shapes:
///   - stable by serial:   "openrgb-s-{sanitized-serial}" (+ "-{zoneIdx}")
///   - stable by location: "openrgb-l-{sanitized-hid-path}" (+ "-{zoneIdx}")
///   - legacy numeric:     "openrgb-N" or "openrgb-N-Z"
/// </summary>
public static class OpenRgbResolver
{
    public static (RgbDevice? device, int zoneIndex) Resolve(string id, IReadOnlyList<RgbDevice> devices)
    {
        if (string.IsNullOrEmpty(id) || devices is null || devices.Count == 0)
            return (null, -1);

        foreach (var d in devices)
        {
            if (string.Equals(d.StableId, id, StringComparison.Ordinal))
                return (d, -1);
        }

        var lastDash = id.LastIndexOf('-');
        if (lastDash > 0 && lastDash < id.Length - 1)
        {
            var prefix = id.AsSpan(0, lastDash);
            var suffix = id.AsSpan(lastDash + 1);
            if (int.TryParse(suffix, out var zoneIdx) && zoneIdx >= 0)
            {
                foreach (var d in devices)
                {
                    if (prefix.SequenceEqual(d.StableId))
                        return (d, zoneIdx);
                }
            }
        }

        var (phys, zone) = ParseDeviceId(id);
        if (phys >= 0)
        {
            foreach (var d in devices)
            {
                if (d.Index == phys)
                    return (d, zone);
            }
        }

        return (null, -1);
    }

    /// <summary>"openrgb-N" returns (N, -1); "openrgb-N-Z" returns (N, Z); other shapes (-1, -1).</summary>
    public static (int physicalIndex, int zoneIndex) ParseDeviceId(string id)
    {
        if (string.IsNullOrEmpty(id) || !id.StartsWith("openrgb-", StringComparison.Ordinal))
            return (-1, -1);
        var tail = id.AsSpan(8);
        var dash = tail.IndexOf('-');
        if (dash < 0)
            return int.TryParse(tail, out var p) ? (p, -1) : (-1, -1);
        if (int.TryParse(tail.Slice(0, dash), out var phys) && int.TryParse(tail.Slice(dash + 1), out var zone))
            return (phys, zone);
        return (-1, -1);
    }
}
