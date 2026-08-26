using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Nexus.Service.Models.Conflicts;

namespace Nexus.Service.Conflicts;

/// <summary>
/// Finds the autostart entries that launch a detected conflicting app, and
/// removes them on request.
///
/// Entries are matched by target executable, never by name: the entry name is
/// unrelated to the process name and is often version-stamped (iCUE runs as
/// "iCUE" from a Run value called "Corsair iCUE5 Software"). Name matching also
/// collides with unrelated system entries - "NZXT CAM" substring-matches
/// Windows' own "camsvc". No match means no entry is offered.
///
/// One app can hold several mechanisms at once (iCUE ships an Automatic service
/// and a Run value), so every match is returned and removal clears all of
/// them.
/// </summary>
public static class ConflictAutostartLocator
{
    public const string KindRunKeyUser = "runKeyUser";
    public const string KindRunKeyMachine = "runKeyMachine";
    public const string KindRunKeyMachine32 = "runKeyMachine32";
    public const string KindService = "service";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string MachineRunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string MachineRunKey32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ServicesPath = @"SYSTEM\CurrentControlSet\Services";

    /// <summary>Service Start value for "Automatic"; 3 is "Manual".</summary>
    private const int ServiceStartAutomatic = 2;
    private const int ServiceStartManual = 3;

#if WINDOWS
    /// <summary>Every entry that autostarts this conflict; empty when none resolves to its executable.</summary>
    [SupportedOSPlatform("windows")]
    public static List<ConflictAutostartEntry> Find(DetectedConflict conflict, ConflictAppDefinition definition)
    {
        var found = new List<ConflictAutostartEntry>();

        // The catalog names the exact service, so no path match is needed.
        foreach (var service in definition.WindowsServiceNames)
        {
            if (ServiceStartValue(service) == ServiceStartAutomatic)
            {
                found.Add(new ConflictAutostartEntry { Kind = KindService, EntryName = service });
            }
        }

        // Every running process of this app, not just the watcher's pid: one app
        // spans several executables in unrelated trees (SignalRGB's service runs
        // from WhirlwindFX while its Run value points into VortxEngine), and
        // resolving one pid finds only the entries that tree happens to carry.
        var exePaths = ResolveExePaths(conflict, definition);
        if (exePaths.Count == 0)
        {
            return found;
        }

        // Exact first: RunEntries yields HKCU before HKLM, so a same-directory
        // sibling in the user hive would otherwise outrank an exact match in the
        // machine hive and the wrong value would be removed.
        var rows = RunEntries();
        var exact = new List<ConflictAutostartEntry>();
        foreach (var (kind, entry, target) in rows)
        {
            if (exePaths.Any(exe => SamePath(target, exe)))
            {
                exact.Add(new ConflictAutostartEntry { Kind = kind, EntryName = entry });
            }
        }
        if (exact.Count > 0)
        {
            found.AddRange(exact);
            return found;
        }

        foreach (var (kind, entry, target) in rows)
        {
            // Without the existence check any Run value naming a missing file
            // in the same directory matches.
            if (exePaths.Any(exe => SameDirectory(target, exe)) && File.Exists(target))
            {
                found.Add(new ConflictAutostartEntry { Kind = kind, EntryName = entry });
            }
        }
        return found;
    }

    /// <summary>Distinct executable paths of every running process belonging to this app.</summary>
    [SupportedOSPlatform("windows")]
    private static List<string> ResolveExePaths(DetectedConflict conflict, ConflictAppDefinition definition)
    {
        var paths = new List<string>();
        void Add(string? path)
        {
            if (!string.IsNullOrEmpty(path)
                && !paths.Contains(path!, StringComparer.OrdinalIgnoreCase))
            {
                paths.Add(path!);
            }
        }

        Add(ResolveExePath(conflict.Pid));
        foreach (var name in definition.ProcessNames)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch { continue; }
            foreach (var proc in procs)
            {
                try { Add(proc.MainModule?.FileName); }
                catch { /* denied for elevated / cross-session processes */ }
                finally { proc.Dispose(); }
            }
        }
        return paths;
    }

    /// <summary>Removes every entry, returning how many were verified gone; only ever called from an explicit user action.</summary>
    [SupportedOSPlatform("windows")]
    public static int Disable(IEnumerable<ConflictAutostartEntry> entries)
    {
        var removed = 0;
        foreach (var entry in entries)
        {
            if (Disable(entry))
            {
                removed++;
            }
        }
        return removed;
    }

    /// <summary>Removes one entry, verifying the write by re-reading it.</summary>
    [SupportedOSPlatform("windows")]
    public static bool Disable(ConflictAutostartEntry entry)
    {
        try
        {
            switch (entry.Kind)
            {
                case KindService:
                    using (var key = Registry.LocalMachine.OpenSubKey($@"{ServicesPath}\{entry.EntryName}", writable: true))
                    {
                        if (key is null) return false;
                        key.SetValue("Start", ServiceStartManual, RegistryValueKind.DWord);
                    }
                    return ServiceStartValue(entry.EntryName) == ServiceStartManual;
                case KindRunKeyUser:
                    var sid = Nexus.Service.Lifecycle.ConsoleUserSid.Resolve(RunKeyPath);
                    if (sid is null) return false;
                    return DeleteRunValue(Registry.Users, $@"{sid}\{RunKeyPath}", entry.EntryName);
                case KindRunKeyMachine:
                    return DeleteRunValue(Registry.LocalMachine, MachineRunKey, entry.EntryName);
                case KindRunKeyMachine32:
                    return DeleteRunValue(Registry.LocalMachine, MachineRunKey32, entry.EntryName);
                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[conflicts] autostart disable failed for {entry.EntryName}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Deletes one Run value and confirms it is gone; DeleteValue with throwOnMissingValue false also succeeds on a key that never held it.</summary>
    [SupportedOSPlatform("windows")]
    private static bool DeleteRunValue(RegistryKey root, string path, string name)
    {
        using (var key = root.OpenSubKey(path, writable: true))
        {
            if (key is null) return false;
            if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is null) return false;
            key.DeleteValue(name, throwOnMissingValue: false);
        }
        using var verify = root.OpenSubKey(path);
        return verify?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is null;
    }

    [SupportedOSPlatform("windows")]
    private static int ServiceStartValue(string serviceName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{ServicesPath}\{serviceName}");
            return key?.GetValue("Start") is int start ? start : -1;
        }
        catch { return -1; }
    }

    [SupportedOSPlatform("windows")]
    private static string? ResolveExePath(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return proc.MainModule?.FileName;
        }
        catch
        {
            // Denied for cross-session / elevated processes; without a path
            // nothing is offered rather than guessed.
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static List<(string Kind, string Entry, string Target)> RunEntries()
    {
        var rows = new List<(string, string, string)>();
        var sid = Nexus.Service.Lifecycle.ConsoleUserSid.Resolve(RunKeyPath);
        if (sid is not null)
        {
            rows.AddRange(ReadRunKey(Registry.Users, $@"{sid}\{RunKeyPath}", KindRunKeyUser));
        }
        rows.AddRange(ReadRunKey(Registry.LocalMachine, MachineRunKey, KindRunKeyMachine));
        rows.AddRange(ReadRunKey(Registry.LocalMachine, MachineRunKey32, KindRunKeyMachine32));
        return rows;
    }

    [SupportedOSPlatform("windows")]
    private static List<(string Kind, string Entry, string Target)> ReadRunKey(RegistryKey root, string path, string kind)
    {
        var rows = new List<(string, string, string)>();
        try
        {
            using var key = root.OpenSubKey(path);
            if (key is null) return rows;
            var profile = ConsoleUserProfilePath();
            foreach (var name in key.GetValueNames())
            {
                // RegistryKey expands a REG_EXPAND_SZ against the calling
                // process's environment; under LocalSystem %LOCALAPPDATA%
                // resolves to config\systemprofile and no per-user install matches.
                if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string value
                    && !string.IsNullOrWhiteSpace(value))
                {
                    rows.Add((kind, name, ExecutablePath(ExpandForConsoleUser(value, profile))));
                }
            }
        }
        catch { /* an unreadable hive yields no candidates */ }
        return rows;
    }

    [SupportedOSPlatform("windows")]
    private static string? ConsoleUserProfilePath()
    {
        try { return Nexus.Service.Lifecycle.ConsoleUserSid.ResolveProfilePath(); }
        catch { return null; }
    }
#else
    /// <summary>Autostart discovery is Windows-only; other platforms resolve nothing.</summary>
    public static List<ConflictAutostartEntry> Find(DetectedConflict conflict, ConflictAppDefinition definition) => new();

    public static int Disable(IEnumerable<ConflictAutostartEntry> entries) => 0;

    public static bool Disable(ConflictAutostartEntry entry) => false;
#endif

    /// <summary>Expands a Run value's per-user variables against the console user's profile; machine-scoped ones resolve alike for every account and are left to the platform expander.</summary>
    internal static string ExpandForConsoleUser(string value, string? profilePath)
    {
        var expanded = value;
        if (!string.IsNullOrEmpty(profilePath))
        {
            var profile = profilePath!.TrimEnd('\\');
            expanded = ReplaceVariable(expanded, "%LOCALAPPDATA%", $@"{profile}\AppData\Local");
            expanded = ReplaceVariable(expanded, "%APPDATA%", $@"{profile}\AppData\Roaming");
            expanded = ReplaceVariable(expanded, "%USERPROFILE%", profile);
        }
        return OperatingSystem.IsWindows() ? Environment.ExpandEnvironmentVariables(expanded) : expanded;
    }

    private static string ReplaceVariable(string value, string name, string replacement)
        => value.Replace(name, replacement, StringComparison.OrdinalIgnoreCase);

    /// <summary>The executable out of a Run command line, dropping quotes and arguments.</summary>
    internal static string ExecutablePath(string command)
    {
        var value = command.Trim();
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            return end > 1 ? value.Substring(1, end - 1) : value.Trim('"');
        }
        // Unquoted paths may still contain spaces, so cut at the .exe rather
        // than at the first space.
        var exe = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe >= 0 ? value.Substring(0, exe + 4) : value;
    }

    /// <summary>Whether an entry's target is the running executable; separators are handled here because Path splits on '\' only on Windows.</summary>
    internal static bool SamePath(string target, string exePath)
    {
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(exePath))
        {
            return false;
        }
        return string.Equals(Normalize(target), Normalize(exePath), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether an entry's target sits beside the running executable, or in a directory containing it; a shared or system directory never qualifies, since every unrelated app under it would match too.</summary>
    internal static bool SameDirectory(string target, string exePath)
    {
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(exePath))
        {
            return false;
        }
        var targetDir = DirectoryOf(Normalize(target));
        var exeDir = DirectoryOf(Normalize(exePath));
        if (targetDir.Length == 0 || IsSharedDirectory(targetDir))
        {
            return false;
        }
        // Beside the running executable, or in a directory containing it: a
        // version-agnostic stub launcher sits above the versioned install
        // (SignalRGB runs from VortxEngine\app-<ver>\ but its Run value points
        // at VortxEngine\SignalRgbLauncher.exe).
        return string.Equals(targetDir, exeDir, StringComparison.OrdinalIgnoreCase)
            || exeDir.StartsWith(targetDir + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A directory that holds programs from more than one vendor.</summary>
    internal static bool IsSharedDirectory(string directory)
    {
        var dir = Normalize(directory);
        // "C:\" and anything shorter is a drive root.
        if (dir.Length <= 3)
        {
            return true;
        }
        // Common Files and the Windows tree, at any depth.
        if (dir.EndsWith(@"\Common Files", StringComparison.OrdinalIgnoreCase)
            || dir.Contains(@"\Common Files\", StringComparison.OrdinalIgnoreCase)
            || dir.EndsWith(@"\Windows", StringComparison.OrdinalIgnoreCase)
            || dir.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        foreach (var tail in SharedRootTails)
        {
            if (dir.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        // A bare profile root has nothing after the account segment.
        var users = dir.IndexOf(@"\Users\", StringComparison.OrdinalIgnoreCase);
        if (users >= 0 && dir.IndexOf('\\', users + 7) < 0)
        {
            return true;
        }
        return false;
    }

    private static readonly string[] SharedRootTails =
    {
        @"\Program Files",
        @"\Program Files (x86)",
        @"\ProgramData",
        @"\AppData\Local",
        @"\AppData\Roaming",
        @"\AppData\Local\Programs",
        @"\AppData\LocalLow",
        @"\Desktop",
        @"\Downloads",
    };

    private static string Normalize(string windowsPath)
        => windowsPath.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');

    private static string DirectoryOf(string windowsPath)
    {
        var cut = windowsPath.LastIndexOf('\\');
        return cut > 0 ? windowsPath.Substring(0, cut) : "";
    }
}
