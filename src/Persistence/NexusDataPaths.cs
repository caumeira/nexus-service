using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Nexus.Service.Persistence;

/// <summary>
/// Single resolver for the machine-scope config root the service's ADO.NET
/// stores (screen time, metrics/temperature history) live under. Extracted
/// from the byte-identical resolvers each SQLite-backed store carried before
/// this shared root existed.
///
/// Distinct from MediaLibrary.NexusDataDir(): that resolver targets the DATA
/// root (XDG_DATA_HOME on Linux) for device media; this one targets the
/// CONFIG root (XDG_CONFIG_HOME on Linux) these SQLite stores have always
/// used. Windows and macOS resolve to the same directory either way, so the
/// two roots only diverge on Linux.
/// </summary>
internal static class NexusDataPaths
{
    /// <summary><c>&lt;config-root&gt;/Nexus</c>: %ProgramData%\Nexus (Windows,
    /// machine-scope since the service runs as LocalSystem),
    /// ~/Library/Application Support/Nexus (macOS), $XDG_CONFIG_HOME/Nexus or
    /// ~/.config/Nexus (Linux).</summary>
    public static string NexusRoot()
    {
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

    /// <summary>Shared SQLite database directory every ADO.NET store's file lives
    /// under: <c>&lt;NexusRoot&gt;/db</c>.</summary>
    public static string DatabaseDir() => Path.Combine(NexusRoot(), "db");
}
