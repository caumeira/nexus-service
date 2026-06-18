using System;
using System.Collections.Generic;
using System.IO;

namespace Nexus.Service.Lighting;

public static class SteamLibraryLocator
{
    public static string? ResolveSteamRoot()
    {
#if WINDOWS
        try
        {
            var regPath = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            if (regPath is not null && Directory.Exists(regPath))
            {
                return regPath;
            }
        }
        catch
        {
            // fall through to HKLM fallbacks
        }

        // HKCU is the SYSTEM account's hive under LocalSystem; read the
        // machine-wide install path instead.
        foreach (var hklmKey in new[]
        {
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam",
        })
        {
            try
            {
                var hklmPath = Microsoft.Win32.Registry.GetValue(hklmKey, "InstallPath", null) as string;
                if (hklmPath is not null && Directory.Exists(hklmPath))
                {
                    return hklmPath;
                }
            }
            catch
            {
                // fall through to next key
            }
        }
#endif
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsLinux())
        {
            var linuxPath = Path.Combine(home, ".steam", "steam");
            if (Directory.Exists(linuxPath))
            {
                return linuxPath;
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            var macPath = Path.Combine(home, "Library", "Application Support", "Steam");
            if (Directory.Exists(macPath))
            {
                return macPath;
            }
        }

        return null;
    }

    public static IEnumerable<string> EnumerateLibraryPaths()
    {
        var steamRoot = ResolveSteamRoot();
        if (steamRoot is null)
        {
            yield break;
        }

        yield return steamRoot;

        var vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdfPath))
        {
            yield break;
        }

        foreach (var rawLine in File.ReadLines(vdfPath))
        {
            var line = rawLine.Trim();
            var key = ReadFirstQuoted(line);
            if (!key.Equals("path", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = ReadSecondQuoted(line);
            if (value.Length > 0 && Directory.Exists(value))
            {
                yield return value;
            }
        }
    }

    public static string? FindAppInstallDir(string appId)
    {
        foreach (var library in EnumerateLibraryPaths())
        {
            var appsDir = Path.Combine(library, "steamapps");
            var manifest = Path.Combine(appsDir, $"appmanifest_{appId}.acf");
            if (!File.Exists(manifest))
            {
                continue;
            }

            try
            {
                foreach (var rawLine in File.ReadLines(manifest))
                {
                    var line = rawLine.Trim();
                    var key = ReadFirstQuoted(line);
                    if (!key.Equals("installdir", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var installDir = ReadSecondQuoted(line);
                    if (installDir.Length == 0)
                    {
                        continue;
                    }

                    var fullPath = Path.Combine(library, "steamapps", "common", installDir);
                    if (Directory.Exists(fullPath))
                    {
                        return fullPath;
                    }
                }
            }
            catch
            {
                // manifest unreadable
            }
        }

        return null;
    }

    private static string ReadFirstQuoted(string line)
    {
        var start = line.IndexOf('"');
        if (start < 0)
        {
            return "";
        }

        var end = line.IndexOf('"', start + 1);
        return end > start ? line.Substring(start + 1, end - start - 1) : "";
    }

    private static string ReadSecondQuoted(string line)
    {
        var first = line.IndexOf('"');
        if (first < 0)
        {
            return "";
        }

        var firstEnd = line.IndexOf('"', first + 1);
        if (firstEnd < 0)
        {
            return "";
        }

        var second = line.IndexOf('"', firstEnd + 1);
        if (second < 0)
        {
            return "";
        }

        var secondEnd = line.IndexOf('"', second + 1);
        return secondEnd > second ? line.Substring(second + 1, secondEnd - second - 1) : "";
    }
}
