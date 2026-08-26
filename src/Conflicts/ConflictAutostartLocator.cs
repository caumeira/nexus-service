using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Nexus.Service.Models.Conflicts;

namespace Nexus.Service.Conflicts;

/// <summary>
/// Finds the autostart entry that launches a detected conflicting app, and
/// removes it on request.
///
/// Entries are matched by target executable, never by name: the entry name is
/// unrelated to the process name and is often version-stamped (iCUE runs as
/// "iCUE" from a Run value called "Corsair iCUE5 Software"; Razer Synapse runs
/// as "RazerAppEngine"). Name matching also collides with unrelated system
/// entries - "NZXT CAM" substring-matches Windows' own "camsvc". No match means
/// no entry is offered, so a wrong one can never be removed.
/// </summary>
public static class ConflictAutostartLocator
{
    public const string KindRunKeyUser = "runKeyUser";
    public const string KindRunKeyMachine = "runKeyMachine";
    public const string KindService = "service";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string MachineRunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string MachineRunKey32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ServicesPath = @"SYSTEM\CurrentControlSet\Services";

    /// <summary>Service Start value for "Automatic"; 3 is "Manual".</summary>
    private const int ServiceStartAutomatic = 2;
    private const int ServiceStartManual = 3;

#if WINDOWS
    /// <summary>The entry that autostarts this conflict, or null when none resolves to its executable.</summary>
    [SupportedOSPlatform("windows")]
    public static ConflictAutostartEntry? Find(DetectedConflict conflict, ConflictAppDefinition definition)
    {
        // A curated Automatic service is the autostart for apps whose background
        // service holds the hardware; it needs no path match because the catalog
        // already names the exact service.
        foreach (var service in definition.WindowsServiceNames)
        {
            if (ServiceStartValue(service) == ServiceStartAutomatic)
            {
                return new ConflictAutostartEntry { Kind = KindService, EntryName = service };
            }
        }

        var exePath = ResolveExePath(conflict.Pid);
        if (string.IsNullOrEmpty(exePath))
        {
            return null;
        }

        foreach (var (kind, entry, target) in RunEntries())
        {
            if (SameProgram(target, exePath!))
            {
                return new ConflictAutostartEntry { Kind = kind, EntryName = entry };
            }
        }
        return null;
    }

    /// <summary>Removes the entry. Only ever called from an explicit user action.</summary>
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
                        return true;
                    }
                case KindRunKeyUser:
                    var sid = Nexus.Service.Lifecycle.ConsoleUserSid.Resolve(RunKeyPath);
                    if (sid is null) return false;
                    using (var key = Registry.Users.OpenSubKey($@"{sid}\{RunKeyPath}", writable: true))
                    {
                        if (key is null) return false;
                        key.DeleteValue(entry.EntryName, throwOnMissingValue: false);
                        return true;
                    }
                case KindRunKeyMachine:
                    foreach (var path in new[] { MachineRunKey, MachineRunKey32 })
                    {
                        using var key = Registry.LocalMachine.OpenSubKey(path, writable: true);
                        if (key?.GetValue(entry.EntryName) is not null)
                        {
                            key.DeleteValue(entry.EntryName, throwOnMissingValue: false);
                            return true;
                        }
                    }
                    return false;
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
    private static IEnumerable<(string Kind, string Entry, string Target)> RunEntries()
    {
        var sid = Nexus.Service.Lifecycle.ConsoleUserSid.Resolve(RunKeyPath);
        if (sid is not null)
        {
            foreach (var row in ReadRunKey(Registry.Users, $@"{sid}\{RunKeyPath}", KindRunKeyUser))
            {
                yield return row;
            }
        }
        foreach (var path in new[] { MachineRunKey, MachineRunKey32 })
        {
            foreach (var row in ReadRunKey(Registry.LocalMachine, path, KindRunKeyMachine))
            {
                yield return row;
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static List<(string Kind, string Entry, string Target)> ReadRunKey(RegistryKey root, string path, string kind)
    {
        var rows = new List<(string, string, string)>();
        try
        {
            using var key = root.OpenSubKey(path);
            if (key is null) return rows;
            foreach (var name in key.GetValueNames())
            {
                if (key.GetValue(name) is string value && !string.IsNullOrWhiteSpace(value))
                {
                    rows.Add((kind, name, ExecutablePath(value)));
                }
            }
        }
        catch { /* an unreadable hive yields no candidates */ }
        return rows;
    }
#else
    /// <summary>Autostart discovery is Windows-only; other platforms resolve nothing.</summary>
    public static ConflictAutostartEntry? Find(DetectedConflict conflict, ConflictAppDefinition definition) => null;

    public static bool Disable(ConflictAutostartEntry entry) => false;
#endif

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

    /// <summary>
    /// Whether an entry's target is the same program as the running executable.
    /// Both are Windows paths whatever the host, so separators are handled here
    /// rather than through Path, which splits on '\' only on Windows.
    /// </summary>
    internal static bool SameProgram(string target, string exePath)
    {
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(exePath))
        {
            return false;
        }
        var a = target.Trim().Replace('/', '\\').TrimEnd('\\');
        var b = exePath.Trim().Replace('/', '\\').TrimEnd('\\');
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        // A launcher in the same install directory (iCUE runs as iCUE.exe from a
        // Run value pointing at its sibling "iCUE Launcher.exe").
        var aDir = DirectoryOf(a);
        var bDir = DirectoryOf(b);
        return aDir.Length > 0 && string.Equals(aDir, bDir, StringComparison.OrdinalIgnoreCase);
    }

    private static string DirectoryOf(string windowsPath)
    {
        var cut = windowsPath.LastIndexOf('\\');
        return cut > 0 ? windowsPath.Substring(0, cut) : "";
    }
}
