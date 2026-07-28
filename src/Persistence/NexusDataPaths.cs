using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Nexus.Service.Persistence;

/// <summary>
/// Single resolver for the machine-scope config root the service's history
/// stores (screen time, metrics/temperature history) live under.
///
/// Distinct from MediaLibrary.NexusDataDir(): that resolver targets the DATA
/// root (XDG_DATA_HOME on Linux) for device media; this one targets the
/// CONFIG root (XDG_CONFIG_HOME on Linux) these history stores have always
/// used. Windows and macOS resolve to the same directory either way, so the
/// two roots only diverge on Linux.
/// </summary>
internal static class NexusDataPaths
{
    /// <summary><c>&lt;config-root&gt;/Nexus</c>: %ProgramData%\Nexus (Windows,
    /// machine-scope since the service runs as LocalSystem),
    /// ~/Library/Application Support/Nexus (macOS), $XDG_CONFIG_HOME/Nexus or
    /// ~/.config/Nexus (Linux). NEXUS_DATA_ROOT, when set to a non-blank
    /// value, takes precedence on every platform so a verification host can
    /// point at a throwaway directory instead of the real machine store.</summary>
    public static string NexusRoot() => ResolveRoot(Environment.GetEnvironmentVariable("NEXUS_DATA_ROOT"));

    /// <summary>Shared database directory every history store's files live
    /// under: <c>&lt;NexusRoot&gt;/db</c>.</summary>
    public static string DatabaseDir() => Path.Combine(NexusRoot(), "db");

    /// <summary>Test seam: resolves the root from an explicit override value
    /// instead of reading the environment, so tests never mutate process-wide
    /// state.</summary>
    internal static string ResolveRoot(string? overrideRoot)
    {
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            return Path.GetFullPath(overrideRoot.Trim());
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", "Nexus");
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, "Nexus");
        }
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(xdg))
        {
            xdg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }
        return Path.Combine(xdg, "Nexus");
    }
}
