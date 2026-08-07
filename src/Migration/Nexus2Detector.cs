#if WINDOWS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Nexus.Service.Lifecycle;
#endif

namespace Nexus.Service.Migration;

/// <summary>Detection ladder result for a Nexus 2 (legacy HYTE Nexus) install.</summary>
public sealed record Nexus2DetectionResult(bool Detected, bool ImportAvailable, string? Version, bool AutostartTaskPresent)
{
    public static readonly Nexus2DetectionResult None = new(false, false, null, false);
}

/// <summary>
/// Detects a legacy HYTE Nexus (Nexus 2) install and offers coexistence
/// actions. Nexus 2 was Windows-only, so the non-Windows implementation is a
/// stub. Every check in <see cref="Detect"/> is read-only and independent -
/// one failing (missing key, unloaded hive, locked file) never blocks the
/// others.
/// </summary>
public interface INexus2Detector
{
    Nexus2DetectionResult Detect();

    /// <summary>Deletes the Nexus 2 autostart scheduled task. Returns true when
    /// the task was deleted or was already absent (idempotent).</summary>
    bool DisableAutostart();
}

#if WINDOWS
public sealed class Nexus2Detector : INexus2Detector
{
    // electron-builder UUIDv5 of Nexus 2's appId com.hyte.desktop.
    private const string UninstallGuid = "95721908-4b6d-50cf-8ca2-e0db9e34d737";
    // Existence gate for ConsoleUserSid.Resolve - always present on any real
    // profile, unlike the GUID subkey which only exists when Nexus 2 is installed.
    private const string HkuUninstallSubPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string HkuUninstallKeyPath = HkuUninstallSubPath + @"\" + UninstallGuid;
    private const string HklmUninstall64Path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + UninstallGuid;
    private const string HklmUninstall32Path = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\" + UninstallGuid;
    private const string HyteIoServiceKey = @"SYSTEM\CurrentControlSet\Services\HYTEIO";
    private const string TaskName = "HYTE Nexus";
    private const string ConfigJsonRelativePath = @"AppData\Roaming\HYTE Nexus\config.json";
    private const string AppConfigJsonRelativePath = @"Documents\Hyte Nexus\appConfig.json";
    private static readonly string[] ProcessNames = { "HYTE Nexus", "HYTE.Nexus.Service" };

    public Nexus2DetectionResult Detect()
    {
        var (arpFound, version) = CheckArpUninstallKey();
        var configJsonFound = ResolveExistingProfileFile(ConfigJsonRelativePath, requireNonEmpty: true) is not null;
        var taskFileFound = ScheduledTaskFileExists();
        var hyteIoFound = CheckHyteIoService();
        var appConfigFound = ResolveExistingProfileFile(AppConfigJsonRelativePath, requireNonEmpty: false) is not null;
        var processFound = CheckProcessRunning();

        var detected = arpFound || configJsonFound || taskFileFound || hyteIoFound || appConfigFound || processFound;
        return new Nexus2DetectionResult(detected, configJsonFound, version, taskFileFound);
    }

    public bool DisableAutostart()
    {
        if (!ScheduledTaskFileExists())
        {
            return true;
        }
        return RunSchtasksDelete();
    }

    private static (bool Found, string? Version) CheckArpUninstallKey()
    {
        var found = false;
        string? version = null;

        try
        {
            var sid = ConsoleUserSid.Resolve(HkuUninstallSubPath);
            if (sid is not null)
            {
                using var key = Registry.Users.OpenSubKey($@"{sid}\{HkuUninstallKeyPath}");
                if (key is not null)
                {
                    found = true;
                    version = key.GetValue("DisplayVersion") as string;
                }
            }
        }
        catch { /* per-check swallow */ }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(HklmUninstall64Path);
            if (key is not null)
            {
                found = true;
                version ??= key.GetValue("DisplayVersion") as string;
            }
        }
        catch { /* per-check swallow */ }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(HklmUninstall32Path);
            if (key is not null)
            {
                found = true;
                version ??= key.GetValue("DisplayVersion") as string;
            }
        }
        catch { /* per-check swallow */ }

        return (found, version);
    }

    private static bool ScheduledTaskFileExists()
    {
        try
        {
            return File.Exists(Path.Combine(Environment.SystemDirectory, "Tasks", TaskName));
        }
        catch
        {
            return false;
        }
    }

    private static bool CheckHyteIoService()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(HyteIoServiceKey);
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool CheckProcessRunning()
    {
        try
        {
            foreach (var name in ProcessNames)
            {
                var procs = Process.GetProcessesByName(name);
                var any = procs.Length > 0;
                foreach (var p in procs)
                {
                    p.Dispose();
                }
                if (any)
                {
                    return true;
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string? ResolveExistingProfileFile(string relativePath, bool requireNonEmpty)
    {
        try
        {
            foreach (var profileDir in CandidateProfileDirs())
            {
                var candidate = Path.Combine(profileDir, relativePath);
                if (!SafeFileExists(candidate))
                {
                    continue;
                }
                if (requireNonEmpty && SafeFileLength(candidate) == 0)
                {
                    continue;
                }
                return candidate;
            }
        }
        catch { /* per-check swallow */ }
        return null;
    }

    // Tries the resolved console user's own profile first, then falls back to
    // the ElgatoProfileLocator-style scan of every real profile under
    // <sysdrive>\Users (console user unresolvable, or Nexus 2 ran under a
    // different account), since the service runs as LocalSystem.
    private static IEnumerable<string> CandidateProfileDirs()
    {
        string? consoleProfile = null;
        try
        {
            consoleProfile = ConsoleUserSid.ResolveProfilePath();
        }
        catch { /* fall through to scan */ }

        if (!string.IsNullOrEmpty(consoleProfile))
        {
            yield return consoleProfile;
        }

        string? usersRoot = null;
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory);
            if (!string.IsNullOrEmpty(root))
            {
                usersRoot = Path.Combine(root, "Users");
            }
        }
        catch { /* ignore */ }
        if (usersRoot is null || !SafeDirExists(usersRoot))
        {
            yield break;
        }

        string[] profiles;
        try
        {
            profiles = Directory.GetDirectories(usersRoot);
        }
        catch
        {
            yield break;
        }

        foreach (var profile in profiles)
        {
            var leaf = Path.GetFileName(profile);
            if (leaf is "Public" or "Default" or "Default User" or "All Users")
            {
                continue;
            }
            if (string.Equals(profile, consoleProfile, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            yield return profile;
        }
    }

    private static bool SafeDirExists(string path)
    {
        try { return Directory.Exists(path); } catch { return false; }
    }

    private static bool SafeFileExists(string path)
    {
        try { return File.Exists(path); } catch { return false; }
    }

    private static long SafeFileLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    private static bool RunSchtasksDelete() =>
        Nexus.Service.Platform.ShellExecutor.RunExit("schtasks.exe", 10000, "/Delete", "/TN", TaskName, "/F") == 0;
}
#else
public sealed class Nexus2Detector : INexus2Detector
{
    public Nexus2DetectionResult Detect() => Nexus2DetectionResult.None;

    public bool DisableAutostart() => false;
}
#endif
