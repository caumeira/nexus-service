using System;
using System.Collections.Generic;

namespace Nexus.Service.Activity;

/// <summary>
/// Names refused by the monitoring sidebar's kill / reveal-in-file-manager
/// actions, regardless of platform: Nexus's own processes (killing its own
/// service/overlay/OpenRGB subprocess this way wedges the app instead of
/// stopping it cleanly) plus a conservative set of Windows session-critical
/// processes whose loss can black-screen or sign out the session.
/// explorer.exe is deliberately not on this list - restarting it is a
/// normal, already user-exposed (Task Manager) troubleshooting action.
/// </summary>
public static class ProcessActionGuards
{
    private static readonly HashSet<string> Denylisted = new(StringComparer.OrdinalIgnoreCase)
    {
        "Nexus", "nexus-overlay", "OpenRGB-headless",
        "System", "Registry",
        "csrss", "wininit", "winlogon", "services", "lsass", "smss", "svchost", "dwm",
    };

    public static bool IsDenylisted(string processName) => Denylisted.Contains(processName.Trim());
}
