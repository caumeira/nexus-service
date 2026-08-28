using System;
using System.Collections.Generic;

namespace Nexus.Service.Conflicts;

/// <summary>
/// Which catalog apps compete for an OpenRGB device, resolved by the vendor
/// string the daemon reports. Universal RGB apps
/// (<see cref="ConflictAppDefinition.ClaimsAllRgb"/>) join every result. The
/// SPA joins these ids against the detected-conflict list to show, per running
/// app, the devices it and Nexus are both after. Curated handlers map through
/// <see cref="Devices.DeviceControlPolicy"/> instead.
/// </summary>
public static class ConflictDeviceOwnership
{
    /// <summary>Catalog ids competing for an OpenRGB device with this vendor string. Empty for an empty vendor.</summary>
    public static List<string> AppIdsForVendor(string? vendor)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(vendor))
            return result;
        foreach (var def in ConflictAppCatalog.All)
        {
            if (def.ClaimsAllRgb)
            {
                result.Add(def.Id);
                continue;
            }
            foreach (var v in def.Vendors)
            {
                if (vendor.Contains(v, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(def.Id);
                    break;
                }
            }
        }
        return result;
    }
}
