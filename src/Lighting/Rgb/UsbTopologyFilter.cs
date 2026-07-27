using System;
using System.Collections.Generic;
using System.Globalization;
using Nexus.Service.Devices;

namespace Nexus.Service.Lighting.Rgb;

/// <summary>
/// Decides whether a USB topology change warrants an OpenRGB subprocess bounce.
/// Devices are counted per vid/pid/name/serial key, so an equal-count swap and
/// a second key-identical unit (serial-less hardware) both read as changes; a
/// diff is relevant only when a changed key's vendor id is in the OpenRGB
/// detector catalog. An empty catalog fails open (every change is relevant),
/// preserving the old bounce-on-anything behavior.
/// </summary>
public static class UsbTopologyFilter
{
    public static Dictionary<string, int> BuildKeys(IReadOnlyList<UsbDeviceEntry> entries)
    {
        var keys = new Dictionary<string, int>(entries.Count, StringComparer.Ordinal);
        foreach (var e in entries)
        {
            var key = $"{e.VendorId:X4}:{e.ProductId:X4}:{e.Name}:{e.Serial}";
            keys[key] = keys.TryGetValue(key, out var n) ? n + 1 : 1;
        }
        return keys;
    }

    public static (int Added, int Removed, bool Relevant) Classify(
        IReadOnlyDictionary<string, int> previous, IReadOnlyDictionary<string, int> current, IReadOnlySet<int> rgbVendorIds)
    {
        var added = 0;
        var removed = 0;
        var relevant = false;
        foreach (var kv in current)
        {
            previous.TryGetValue(kv.Key, out var before);
            if (kv.Value > before)
            {
                added += kv.Value - before;
                relevant |= IsRelevant(kv.Key, rgbVendorIds);
            }
        }
        foreach (var kv in previous)
        {
            current.TryGetValue(kv.Key, out var after);
            if (kv.Value > after)
            {
                removed += kv.Value - after;
                relevant |= IsRelevant(kv.Key, rgbVendorIds);
            }
        }
        return (added, removed, relevant);
    }

    private static bool IsRelevant(string key, IReadOnlySet<int> rgbVendorIds)
    {
        if (rgbVendorIds.Count == 0)
        {
            return true;
        }
        return key.Length >= 4
            && int.TryParse(key.AsSpan(0, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var vid)
            && rgbVendorIds.Contains(vid);
    }
}
