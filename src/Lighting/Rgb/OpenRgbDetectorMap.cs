using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;

namespace Nexus.Service.Lighting.Rgb;

/// <summary>
/// Reads <c>detector-map.json</c>, which the bundled daemon rewrites at the end
/// of every detection pass: device name -> the name of the REGISTER_*_DETECTOR
/// entry that produced it.
///
/// The two names are not the same thing and the SDK only carries the first.
/// Table-driven detectors register one generic string and emit per-model names
/// ("Corsair DRAM" -> "Corsair Vengeance RGB DDR5"), and the OpenRGB.json
/// denylist is keyed by the detector name, so an exclusion snapshotted from the
/// device name is a silent no-op: the detector keeps running and keeps claiming
/// the hardware (for DRAM, off another app's SMBus). HID devices mostly name
/// their detector after the model, which is why exclusions worked at all.
/// </summary>
public static class OpenRgbDetectorMap
{
    /// <summary>
    /// Loads the map, or an empty one when the file is absent or unreadable -
    /// callers then fall back to the device name, which is the pre-map
    /// behaviour. A daemon predating the map never writes the file.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Load(string configDir)
    {
        try
        {
            var path = Path.Combine(configDir, "detector-map.json");
            if (!File.Exists(path))
            {
                return EmptyMap;
            }
            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[openrgb] detector map read failed: {ex.Message}");
            return EmptyMap;
        }
    }

    /// <summary>Parses the document body. Separate from the read so the shape
    /// is testable without a filesystem.</summary>
    internal static IReadOnlyDictionary<string, string> Parse(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject root || root["devices"] is not JsonObject devices)
        {
            return EmptyMap;
        }
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in devices)
        {
            if (kv.Value is JsonValue value
                && value.TryGetValue<string>(out var detector)
                && !string.IsNullOrEmpty(kv.Key)
                && !string.IsNullOrEmpty(detector))
            {
                map[kv.Key] = detector;
            }
        }
        return map;
    }

    /// <summary>The detector name to denylist for this device name, falling
    /// back to the device name itself when the map has no entry.</summary>
    public static string Resolve(IReadOnlyDictionary<string, string>? map, string deviceName)
        => map is not null && map.TryGetValue(deviceName, out var detector) ? detector : deviceName;

    private static readonly Dictionary<string, string> EmptyMap = new(StringComparer.Ordinal);
}
