using System;
using System.IO;

namespace Qos.Service.Lifecycle;

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
        "Qos", "PawnIO", "PawnIO.sys");

    /// <summary>
    /// PawnIOLib.dll location, preferring system install over bundled copy.
    /// If the user has PawnIO installed via PawnIO_setup.exe, we use their
    /// DLL so it stays version-matched with their kernel driver.
    /// </summary>
    public static string DllPath
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                var systemDll = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "PawnIO", "PawnIOLib.dll");
                if (File.Exists(systemDll))
                {
                    return systemDll;
                }
            }
            return Path.Combine(BundleDir, "PawnIOLib.dll");
        }
    }
}
