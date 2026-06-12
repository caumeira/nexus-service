using System;
using System.Collections.Generic;
using System.Globalization;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting.Zones;

/// <summary>
/// One-time settings migration (schema v5 to v6): moves the per-card
/// LedMapOverrides / LedMapAspectRatios into the device-scoped, segment-local
/// DeviceLedOverrides / DeviceAspectRatios. The legacy card id to
/// (deviceId, segment) mapping is derivable for every provider's default
/// partition; keys that match no known multi-segment shape migrate as
/// single-segment devices (segment zero, deviceId = legacy key).
/// </summary>
public static class LegacyLedOverrideMigration
{
    /// <summary>Upper bound for a numeric card-id tail to be read as a motherboard zone index; larger tails are treated as part of a serial.</summary>
    private const int MaxLegacyZoneIndex = 16;

    public static void Apply(NexusSettings settings)
    {
        var devices = settings.Devices;

        foreach (var (key, overrides) in devices.LedMapOverrides)
        {
            if (overrides is null || overrides.Count == 0)
            {
                continue;
            }
            var (deviceId, segment) = MapLegacyCardId(key);
            if (!devices.DeviceLedOverrides.TryGetValue(deviceId, out var target))
            {
                target = new List<SegmentLedOverride>();
                devices.DeviceLedOverrides[deviceId] = target;
            }
            foreach (var o in overrides)
            {
                target.Add(new SegmentLedOverride
                {
                    Segment = segment,
                    LedIndex = o.LedIndex,
                    U = o.U,
                    V = o.V,
                    Disabled = o.Disabled,
                });
            }
        }
        devices.LedMapOverrides.Clear();

        foreach (var (key, ratio) in devices.LedMapAspectRatios)
        {
            var (deviceId, _) = MapLegacyCardId(key);
            // First mapped card wins when several legacy zone cards collapse
            // onto one device.
            devices.DeviceAspectRatios.TryAdd(deviceId, ratio);
        }
        devices.LedMapAspectRatios.Clear();
    }

    /// <summary>
    /// Legacy card id to (deviceId, segment). Known multi-segment shapes:
    /// keeb keys/underglow suffixes and OpenRGB split-motherboard zone ids
    /// ("{stableId}-{zoneIndex}"). Everything else is a single-card device.
    /// </summary>
    public static (string deviceId, int segment) MapLegacyCardId(string id)
    {
        if (id.StartsWith("keeb:", StringComparison.Ordinal))
        {
            if (id.EndsWith(":keys", StringComparison.Ordinal))
            {
                return (id[..^":keys".Length], 0);
            }
            if (id.EndsWith(":underglow", StringComparison.Ordinal))
            {
                return (id[..^":underglow".Length], 1);
            }
            return (id, 0);
        }

        if (id.StartsWith("openrgb-", StringComparison.Ordinal))
        {
            var lastDash = id.LastIndexOf('-');
            if (lastDash > 0 && lastDash < id.Length - 1)
            {
                var tail = id.AsSpan(lastDash + 1);
                if (int.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out var zone)
                    && zone >= 0 && zone <= MaxLegacyZoneIndex)
                {
                    var prefix = id[..lastDash];
                    if (IsPlausibleOpenRgbDeviceId(prefix))
                    {
                        return (prefix, zone);
                    }
                }
            }
        }

        return (id, 0);
    }

    /// <summary>
    /// True for the three OpenRGB device-id shapes: "openrgb-{n}" (legacy
    /// numeric), "openrgb-s-{serial}", "openrgb-l-{location}". Bare
    /// "openrgb-s" / "openrgb-l" stems are NOT device ids - those occur when
    /// a whole-device card's sanitized serial itself ends in "-{n}".
    /// </summary>
    private static bool IsPlausibleOpenRgbDeviceId(string id)
    {
        if (!id.StartsWith("openrgb-", StringComparison.Ordinal))
        {
            return false;
        }
        var rest = id.AsSpan("openrgb-".Length);
        if (rest.Length == 0)
        {
            return false;
        }
        if (int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return true;
        }
        if ((rest.StartsWith("s-", StringComparison.Ordinal) || rest.StartsWith("l-", StringComparison.Ordinal))
            && rest.Length > 2)
        {
            return true;
        }
        return false;
    }
}
