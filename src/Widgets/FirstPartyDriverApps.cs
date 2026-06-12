using System;
using System.Collections.Generic;

namespace Nexus.Service.Widgets;

/// <summary>
/// Compile-time allowlist of appIds permitted to declare a <c>driver</c> manifest
/// block — i.e. to have the host fetch and run a native sidecar executable on their
/// behalf. Running a downloaded exe (elevated, holding a device handle) is a
/// host-only, first-party/partner capability the SDK forbids for marketplace apps;
/// this list is the gate. Enforced at manifest parse (the registry drops a
/// <c>driver</c> block from any other app) and again at dispatch.
/// </summary>
public static class FirstPartyDriverApps
{
    private static readonly HashSet<string> Ids = new(StringComparer.Ordinal)
    {
        "com.ibuypower.control",
    };

    public static bool Contains(string appId) => Ids.Contains(appId);
}
