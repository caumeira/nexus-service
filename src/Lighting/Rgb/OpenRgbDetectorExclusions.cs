using System;
using System.Collections.Generic;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting.Rgb;

/// <summary>
/// Reconciles the persisted OpenRGB detector-exclusion snapshots against the
/// live device list and the uncontrolled set. A device becomes excluded when
/// every card it emits is uncontrolled AND every live device sharing its
/// OpenRGB name is too (the daemon's denylist is per detector name, i.e. per
/// model); an exclusion lifts when the device's base id leaves the
/// uncontrolled list. Pure compute - the bridge persists the delta and
/// bounces the subprocess to apply it.
/// </summary>
public static class OpenRgbDetectorExclusions
{
    public sealed class Delta
    {
        public List<KeyValuePair<string, OpenRgbDetectorExclusion>> Add { get; } = new();
        public List<string> Remove { get; } = new();
        /// <summary>Base ids appended to the uncontrolled list so a zone-ignored device's synthesized card round-trips its toggle by base id.</summary>
        public List<string> UncontrolledAdds { get; } = new();
        public bool IsEmpty => Add.Count == 0 && Remove.Count == 0 && UncontrolledAdds.Count == 0;
    }

    /// <summary>
    /// Apply a computed delta inside IConfigStore.Update. Collections are
    /// replaced, never mutated in place, so lock-free readers holding the
    /// previous reference (card emission, the process-manager launch path)
    /// never observe a torn state. Lifting an exclusion also purges the
    /// device's zone-suffixed uncontrolled ids ("{base}-{n}" split cards,
    /// "{base}:z{n}" custom zones) - without the purge the re-detected device
    /// is still fully uncontrolled and immediately re-qualifies, reverting the
    /// user's un-ignore within one bounce cycle.
    /// </summary>
    public static void Apply(NexusSettings s, Delta delta)
    {
        if (delta.Add.Count > 0 || delta.Remove.Count > 0)
        {
            var nextExclusions = new Dictionary<string, OpenRgbDetectorExclusion>(s.Devices.OpenRgbDetectorExclusions);
            foreach (var kv in delta.Add)
            {
                nextExclusions[kv.Key] = kv.Value;
            }
            foreach (var key in delta.Remove)
            {
                nextExclusions.Remove(key);
            }
            s.Devices.OpenRgbDetectorExclusions = nextExclusions;
        }

        if (delta.UncontrolledAdds.Count == 0 && delta.Remove.Count == 0)
        {
            return;
        }
        var next = new List<string>(s.Devices.UncontrolledLightingDevices.Count + delta.UncontrolledAdds.Count);
        foreach (var id in s.Devices.UncontrolledLightingDevices)
        {
            var purged = false;
            foreach (var removedBase in delta.Remove)
            {
                if (id == removedBase || IsZoneIdOf(id, removedBase))
                {
                    purged = true;
                    break;
                }
            }
            if (!purged)
            {
                next.Add(id);
            }
        }
        foreach (var id in delta.UncontrolledAdds)
        {
            if (!next.Contains(id))
            {
                next.Add(id);
            }
        }
        s.Devices.UncontrolledLightingDevices = next;
    }

    /// <summary>
    /// True when id is a zone card of baseId: "{base}-{n}" (split default) or
    /// "{base}:z{n}" (custom partition, ZoneResolution.CustomZoneId). The
    /// numeric anchor keeps a sibling device whose serial merely extends
    /// baseId's (id "openrgb-s-MB01-EXT") out of the purge.
    /// </summary>
    private static bool IsZoneIdOf(string id, string baseId)
    {
        if (id.Length <= baseId.Length + 1 || !id.StartsWith(baseId, StringComparison.Ordinal))
        {
            return false;
        }
        var digitsFrom = id[baseId.Length] switch
        {
            '-' => baseId.Length + 1,
            ':' when id.Length > baseId.Length + 2 && id[baseId.Length + 1] == 'z' => baseId.Length + 2,
            _ => -1,
        };
        if (digitsFrom < 0 || digitsFrom >= id.Length)
        {
            return false;
        }
        for (var i = digitsFrom; i < id.Length; i++)
        {
            if (!char.IsAsciiDigit(id[i]))
            {
                return false;
            }
        }
        return true;
    }

    public static Delta Compute(IReadOnlyList<RgbDevice> devices, NexusSettings settings)
    {
        var delta = new Delta();
        var exclusions = settings.Devices.OpenRgbDetectorExclusions;
        var uncontrolled = settings.Devices.UncontrolledLightingDevices;

        foreach (var kv in exclusions)
        {
            if (!uncontrolled.Contains(kv.Key))
            {
                delta.Remove.Add(kv.Key);
            }
        }

        if (devices.Count == 0)
        {
            return delta;
        }

        // Placeholders and phantoms (0 LEDs) never seed an exclusion; neither
        // does an index-fallback id, which OpenRGB reassigns across bounces so
        // the snapshot could not be matched back to the hardware.
        var byName = new Dictionary<string, List<RgbDevice>>(StringComparer.Ordinal);
        foreach (var d in devices)
        {
            if (d.LedCount <= 0 || string.IsNullOrEmpty(d.Name))
            {
                continue;
            }
            if (!byName.TryGetValue(d.Name, out var list))
            {
                list = new List<RgbDevice>();
                byName[d.Name] = list;
            }
            list.Add(d);
        }

        foreach (var group in byName)
        {
            var allExcludable = true;
            foreach (var d in group.Value)
            {
                if (!d.HasStableHardwareId || !OpenRgbZoneSupport.IsFullyUncontrolled(d, settings))
                {
                    allExcludable = false;
                    break;
                }
            }
            if (!allExcludable)
            {
                continue;
            }

            foreach (var d in group.Value)
            {
                var baseId = d.StableId;
                if (exclusions.ContainsKey(baseId))
                {
                    continue;
                }
                delta.Add.Add(new KeyValuePair<string, OpenRgbDetectorExclusion>(baseId, new OpenRgbDetectorExclusion
                {
                    DetectorName = d.Name,
                    Vendor = d.Vendor,
                    Serial = d.Serial,
                    Location = d.Location,
                    LedCount = d.LedCount,
                    Type = d.Type,
                }));
                if (!uncontrolled.Contains(baseId))
                {
                    delta.UncontrolledAdds.Add(baseId);
                }
            }
        }

        return delta;
    }
}
