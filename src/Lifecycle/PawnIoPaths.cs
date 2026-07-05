using System;
using System.IO;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// Resolves PawnIO file paths. The user-mode DLL prefers a system-installed
/// PawnIOLib.dll if PawnIO_setup.exe was run separately by the user, so the
/// DLL stays version-matched with their kernel driver. Falls back to the
/// bundled copy in pawnio/ next to the service exe.
/// </summary>
public static class PawnIoPaths
{
    private static string BundleDir => Path.Combine(AppContext.BaseDirectory, "pawnio");

    /// <summary>Bundled .sys file (read-only source location next to the exe).</summary>
    public static string BundledSysPath => Path.Combine(BundleDir, "PawnIO.sys");

    /// <summary>
    /// Where we copy .sys to before SCM picks it up. Stable, in ProgramData so
    /// it survives a service exe update without re-registering the driver.
    /// </summary>
    public static string InstalledSysPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Nexus", "PawnIO", "PawnIO.sys");

    /// <summary>Marker written when a driver upgrade is bound but pending a reboot.</summary>
    public static string UpgradeMarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Nexus", "PawnIO", "upgrade-pending.json");

    /// <summary>
    /// PawnIOLib.dll location, preferring the system install over the bundled
    /// copy only when its file version is at least the bundled version. This
    /// keeps the DLL from trailing a kernel driver Nexus has upgraded to the
    /// bundled version.
    /// </summary>
    public static string DllPath
    {
        get
        {
            var bundledDll = Path.Combine(BundleDir, "PawnIOLib.dll");
            if (OperatingSystem.IsWindows())
            {
                var systemDll = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "PawnIO", "PawnIOLib.dll");
                if (File.Exists(systemDll))
                {
                    var bundledVersion = PawnIoInstaller.ReadFileVersion(bundledDll);
                    if (bundledVersion is not null
                        && ShouldUseSystemDll(PawnIoInstaller.ReadFileVersion(systemDll), bundledVersion))
                    {
                        return systemDll;
                    }
                }
            }
            return bundledDll;
        }
    }

    internal static bool ShouldUseSystemDll(Version? systemVersion, Version bundledVersion)
    {
        return systemVersion is not null && systemVersion >= bundledVersion;
    }
}
