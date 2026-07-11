using System;
using System.Collections.Generic;
using Nexus.Service.Devices.Handlers;

namespace Nexus.Service.Devices;

/// <summary>
/// Brand policy for the Nexus Control gate. A third-party hub defaults off
/// precisely when a competing vendor app also drives it, so Nexus does not fight
/// that app until the user opts in after closing it (the device page surfaces
/// which app to close). Hyte/iBUYPOWER hardware, streamed panels (which have no
/// on/off row to re-enable), and any handler without a mapped competitor default
/// on. One source of truth for both facts.
/// </summary>
public static class DeviceControlPolicy
{
    private static readonly Dictionary<string, string> ConflictAppByHandler = new(StringComparer.OrdinalIgnoreCase)
    {
        ["lianli"] = "lian-li-l-connect",
        ["lianli-tl"] = "lian-li-l-connect",
        ["lianli-wireless"] = "lian-li-l-connect",
        ["lianli-aio"] = "lian-li-l-connect",
        ["strimer"] = "lian-li-l-connect",
        ["corsair"] = "icue",
        ["tryx"] = "tryx-kanali",
        ["streamdeck"] = StreamDeckHandler.ElgatoConflictAppId,
    };

    public static bool DefaultOn(string handlerId) => !ConflictAppByHandler.ContainsKey(handlerId);

    public static string? ConflictAppFor(string handlerId)
        => ConflictAppByHandler.TryGetValue(handlerId, out var id) ? id : null;
}
