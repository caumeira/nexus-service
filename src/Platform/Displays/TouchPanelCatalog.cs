using System;
using System.Collections.Generic;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Handlers;
using Nexus.Service.Peripherals.Hyte.Y70Display;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// A touch panel model the touch-mapping guard is allowed to repair: the
/// display's EDID hardware-id fragments (matched the same way
/// DisplayTopologyService.IsY70Display does) and the USB VID/PIDs of the
/// digitizer that should target it. Scoped per entry so a digitizer never
/// matches a different model's panel.
/// </summary>
public sealed record TouchPanelCatalogEntry(
    string Family,
    IReadOnlyList<string> DisplayHardwareIdFragments,
    IReadOnlyList<UsbId> DigitizerIds);

/// <summary>
/// Panels the touch-mapping guard knows how to repair. The guard is a no-op
/// for any digitizer or display that doesn't match an entry here.
/// </summary>
public static class TouchPanelCatalog
{
    // Xeneon Edge touch strip: KnownPanelDisplays already asserts Touch=true
    // for it independent of OS association state, but its digitizer VID/PID
    // has not been captured yet. Add a row here once it has.
    private static readonly TouchPanelCatalogEntry[] Entries =
    {
        new("y70", Y70DisplayProtocol.DdcPanelHardwareNames, Y70Handler.TouchDigitizers),
    };

    public static TouchPanelCatalogEntry? MatchDisplay(string monitorInterfacePath)
    {
        if (string.IsNullOrEmpty(monitorInterfacePath)) return null;
        foreach (var entry in Entries)
        {
            foreach (var fragment in entry.DisplayHardwareIdFragments)
            {
                if (monitorInterfacePath.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                    return entry;
            }
        }
        return null;
    }

    public static bool MatchesDigitizer(TouchPanelCatalogEntry entry, string digitizerInterfacePath)
    {
        if (string.IsNullOrEmpty(digitizerInterfacePath)) return false;
        foreach (var id in entry.DigitizerIds)
        {
            var fragment = $"VID_{id.VendorId:X4}&PID_{id.ProductId:X4}";
            if (digitizerInterfacePath.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when the digitizer matches any catalog entry's VID/PID, regardless
    /// of whether that entry's panel display is currently attached. The
    /// generic touch-mapping tier uses this to exclude a catalog digitizer
    /// even when its own panel is absent, so it never gets inferred onto a
    /// different touch-expected display.
    /// </summary>
    public static bool IsKnownDigitizer(string digitizerInterfacePath)
    {
        if (string.IsNullOrEmpty(digitizerInterfacePath)) return false;
        foreach (var entry in Entries)
        {
            if (MatchesDigitizer(entry, digitizerInterfacePath)) return true;
        }
        return false;
    }
}
