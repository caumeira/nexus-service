using Nexus.Service.Common.ExternalTools;
using Nexus.Service.Models.Widgets;
using Xunit;

namespace Nexus.Service.Tests.Common.ExternalTools;

/// <summary>
/// Pins the kill switch for the host-exe driver path. Turning it off is how a device
/// Nexus drives natively keeps a vendor binary off its HID; two writers on one HID
/// render as a panel flickering between hosts rather than as an error.
/// </summary>
public class DriverExePolicyTests
{
    private static AppManifestDriver HostExe() => new() { ToolId = "vendor-tool", DeviceId = "vendor-device" };
    private static AppManifestDriver Adb() => new() { ToolId = "qshell", Target = "android-adb", Package = "com.nexus.qshell" };

    [Fact]
    public void Disabling_blocks_a_host_exe_driver()
    {
        Assert.True(new DriverExePolicy(enabled: false).IsBlocked(HostExe()));
    }

    [Fact]
    public void Ships_with_the_host_exe_driver_path_available()
    {
        // The registration in NexusServiceCollectionExtensions passes enabled: true;
        // this pins what that means for a host-exe driver.
        Assert.False(new DriverExePolicy(enabled: true).IsBlocked(HostExe()));
    }

    [Fact]
    public void Android_adb_drivers_are_never_blocked()
    {
        // An adb driver pushes an APK; it runs no host process and cannot contend
        // for a HID, so a host-exe block is not its problem.
        Assert.False(new DriverExePolicy(enabled: false).IsBlocked(Adb()));
        Assert.False(new DriverExePolicy(enabled: true).IsBlocked(Adb()));
    }

    [Fact]
    public void A_driver_naming_no_target_counts_as_host_exe()
    {
        // Defaulting the other way would silently leave a vendor .exe launching.
        Assert.True(DriverExePolicy.IsHostExe(new AppManifestDriver { ToolId = "x" }));
    }
}
