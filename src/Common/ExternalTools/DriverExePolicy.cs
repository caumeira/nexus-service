using System;
using Nexus.Service.Models.Widgets;

namespace Nexus.Service.Common.ExternalTools;

/// <summary>
/// Whether the service may fetch and launch a vendor driver .exe.
///
/// A kill switch for the host-exe driver path as a whole, for a device Nexus turns out
/// to drive natively: a vendor binary and a native writer on one HID cannot hand the
/// device over, because the binary has no graceful stop
/// (<see cref="HostExeInstallStrategy.Terminate"/> is a hard kill). The narrower fix is
/// to drop the <c>driver</c> block from that app's manifest, which leaves every other
/// app's driver working; reach for this only to disable the path outright.
///
/// Android (adb) drivers push an APK rather than run a host process and are unaffected.
/// </summary>
public sealed class DriverExePolicy
{
    public DriverExePolicy(bool enabled)
    {
        Enabled = enabled;
    }

    public bool Enabled { get; }

    /// <summary>True when this driver runs a host .exe rather than pushing an APK over adb.</summary>
    public static bool IsHostExe(AppManifestDriver driver)
        => !string.Equals(driver.Target, "android-adb", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when this driver must not be fetched, launched, or offered as installable.</summary>
    public bool IsBlocked(AppManifestDriver driver) => !Enabled && IsHostExe(driver);
}
