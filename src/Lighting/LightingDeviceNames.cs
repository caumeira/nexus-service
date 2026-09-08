using System.Collections.Generic;
using Nexus.Service.Models.Devices;

namespace Nexus.Service.Lighting;

/// <summary>
/// Applies the lighting page's user-renamed cards to the device list the SPA
/// reads. Only the /devices/lighting-devices/all route calls this: mappings,
/// telemetry and diagnostics keep reading the provider's hardware name, so a
/// rename never reaches a published community mapping. Mirrors the fan-header
/// rename the cooling routes apply over <see cref="Nexus.Service.Persistence.CoolingSettings.FanNames"/>.
/// </summary>
public static class LightingDeviceNames
{
    /// <summary>
    /// Renames in place - every provider builds its cards fresh per GetAll, so
    /// nothing cached is mutated. The replaced hardware name moves to
    /// <see cref="LightingDevice.OriginalName"/> so the UI can still show it.
    /// </summary>
    public static void Apply(List<LightingDevice> devices, IReadOnlyDictionary<string, string> names)
    {
        if (names.Count == 0) return;
        foreach (var dev in devices)
        {
            if (!names.TryGetValue(dev.Id, out var custom)) continue;
            if (string.IsNullOrWhiteSpace(custom)) continue;
            dev.OriginalName = dev.Name;
            dev.Name = custom;
        }
    }
}
