using System.Runtime.InteropServices;

namespace Nexus.Service.Telemetry;

internal static class TelemetryPlatform
{
    /// <summary>Short OS tag shared by every telemetry channel - the fleet
    /// heartbeat, product events, and the system profile all report the same
    /// spelling. (PingRoutes uses its own win→"windows" form for its public
    /// /ping contract; don't unify the two without a contract change.)</summary>
    public static string OsTag() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "mac"
        : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux"
        : "other";
}
