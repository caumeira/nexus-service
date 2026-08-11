using System.IO;
using System.Threading.Tasks;
#if WINDOWS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.Win32;
using Nexus.Service.Lifecycle;
#endif

namespace Nexus.Service.Migration;

/// <summary>Detection result for a legacy HYTE Nexus (Nexus 2) install.
/// Detected means present now; ImportAvailable and AutostartTaskPresent are
/// leftovers that outlive an uninstall. InstallLocation is the ARP-reported
/// install root, scoping the close-app OpenRGB kill to Nexus 2's own copy.</summary>
public sealed record Nexus2DetectionResult(
    bool Detected, bool ImportAvailable, string? Version, bool AutostartTaskPresent,
    bool Running = false, string? InstallLocation = null)
{
    public static readonly Nexus2DetectionResult None = new(false, false, null, false);
}

/// <summary>Turns the raw probes into a result. Cross-platform (the probes
/// themselves are Windows-only) so the presence-vs-leftover rule stays under
/// test.</summary>
internal static class Nexus2DetectionRules
{
    /// <summary>Nexus 2 is present when its uninstall entry is live or one of
    /// its processes runs. Importable config data and a registered autostart
    /// task survive an uninstall, so neither implies presence.</summary>
    public static Nexus2DetectionResult Compose(
        bool installed, bool running, bool importAvailable, bool autostartTaskPresent,
        string? version, string? installLocation) =>
        new(installed || running, importAvailable, version, autostartTaskPresent, running, installLocation);

    /// <summary>An uninstall entry counts only while the install directory it
    /// records is still on disk; a partial uninstall can leave the entry
    /// behind. An entry recording no location is taken at face value.</summary>
    public static bool UninstallEntryIsLive(string? installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation))
        {
            return true;
        }
        try { return Directory.Exists(installLocation); } catch { return false; }
    }
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

    /// <summary>Closes a running Nexus 2: graceful CloseMainWindow first (the
    /// AW5 failure log entry - hard-killing a process that owns a device HID
    /// can wedge the hardware until reboot), then kills any survivors plus
    /// Nexus 2's own bundled OpenRGB.exe. Idempotent: true when nothing ends
    /// up running, including when nothing was running to begin with.</summary>
    Task<bool> CloseAppAsync();
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
    private const string TaskName = "HYTE Nexus";
    private const string ConfigJsonRelativePath = @"AppData\Roaming\HYTE Nexus\config.json";
    // The Electron main process; the one that owns a top-level window CloseMainWindow can target.
    private const string MainProcessName = "HYTE Nexus";
    private static readonly string[] ProcessNames = { "HYTE Nexus", "HYTE.Nexus.Service" };

    private string _lastLoggedSignals = "";

    public Nexus2DetectionResult Detect()
    {
        var arp = CheckArpUninstallKey();
        var result = Nexus2DetectionRules.Compose(
            installed: arp.Live,
            running: CheckProcessRunning(),
            importAvailable: ResolveExistingProfileFile(ConfigJsonRelativePath) is not null,
            autostartTaskPresent: ScheduledTaskFileExists(),
            version: arp.Version,
            installLocation: arp.InstallLocation);

        LogSignalsWhenChanged(result, arp.Found);
        return result;
    }

    // Which probes fired, so a "Nexus 2 detected but it is not installed"
    // report names its own cause. Logged on change, not per poll.
    private void LogSignalsWhenChanged(Nexus2DetectionResult result, bool uninstallEntryFound)
    {
        if (!uninstallEntryFound && !result.Detected && !result.ImportAvailable && !result.AutostartTaskPresent)
        {
            return;
        }

        var line = $"[nexus2] detected={result.Detected} uninstallEntry={uninstallEntryFound} "
            + $"installDir={result.InstallLocation ?? "(none)"} running={result.Running} "
            + $"configData={result.ImportAvailable} autostartTask={result.AutostartTaskPresent}";
        if (Interlocked.Exchange(ref _lastLoggedSignals, line) != line)
        {
            Console.Error.WriteLine(line);
        }
    }

    public bool DisableAutostart()
    {
        if (!ScheduledTaskFileExists())
        {
            return true;
        }
        return RunSchtasksDelete();
    }

    public async Task<bool> CloseAppAsync()
    {
        try
        {
            await CloseGracefullyThenKillAsync(Process.GetProcessesByName(MainProcessName), TimeSpan.FromSeconds(10));

            foreach (var name in ProcessNames)
            {
                KillSurvivors(Process.GetProcessesByName(name));
            }

            // Nexus's own bundled OpenRGB owns device HID too, so it gets the
            // same graceful-first treatment as the main process (the AW5
            // failure-log entry: a hard kill mid-transaction can wedge the
            // hardware until reboot).
            await CloseGracefullyThenKillAsync(BundledOpenRgbProcesses(), TimeSpan.FromSeconds(5));

            var stillRunning = CheckProcessRunning();
            var leftoverOpenRgb = BundledOpenRgbProcesses();
            KillSurvivors(leftoverOpenRgb); // disposes; nothing left alive to kill at this point
            return !stillRunning && leftoverOpenRgb.Length == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task CloseGracefullyThenKillAsync(Process[] processes, TimeSpan gracePeriod)
    {
        using (var cts = new CancellationTokenSource(gracePeriod))
        {
            foreach (var proc in processes)
            {
                try { if (!proc.HasExited) proc.CloseMainWindow(); }
                catch { /* per-process swallow */ }
            }
            foreach (var proc in processes)
            {
                try { await proc.WaitForExitAsync(cts.Token); }
                catch { /* timed out or already gone; killed as a survivor below */ }
            }
        }
        KillSurvivors(processes);
    }

    private static void KillSurvivors(Process[] processes)
    {
        foreach (var proc in processes)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch { /* per-process swallow */ }
            finally { proc.Dispose(); }
        }
    }

    // Only an OpenRGB.exe whose main module lives under Nexus 2's own install
    // root - never a user's standalone OpenRGB or Nexus's own copy.
    private static Process[] BundledOpenRgbProcesses()
    {
        var root = ResolveInstallRootPrefix();
        if (root is null)
        {
            return Array.Empty<Process>();
        }
        var matches = new List<Process>();
        foreach (var proc in Process.GetProcessesByName("OpenRGB"))
        {
            try
            {
                var modulePath = proc.MainModule?.FileName;
                if (modulePath is not null && modulePath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(proc);
                    continue;
                }
            }
            catch { /* inaccessible module: not confirmed ours, leave it running */ }
            proc.Dispose();
        }
        return matches.ToArray();
    }

    private static string? ResolveInstallRootPrefix()
    {
        var (_, _, _, installLocation) = CheckArpUninstallKey();
        if (string.IsNullOrEmpty(installLocation))
        {
            return null;
        }
        return Path.GetFullPath(installLocation).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
    }

    /// <summary>Live is true once an entry whose own recorded location still
    /// exists is found; the first such entry supplies Version and
    /// InstallLocation, so a stale per-user entry cannot veto a live
    /// machine-wide one.</summary>
    private static (bool Found, bool Live, string? Version, string? InstallLocation) CheckArpUninstallKey()
    {
        var found = false;
        var live = false;
        string? version = null;
        string? installLocation = null;

        void Fold(RegistryKey? key)
        {
            if (key is null)
            {
                return;
            }
            found = true;
            if (live)
            {
                return;
            }
            var location = key.GetValue("InstallLocation") as string;
            if (!Nexus2DetectionRules.UninstallEntryIsLive(location))
            {
                return;
            }
            live = true;
            version = key.GetValue("DisplayVersion") as string;
            installLocation = location;
        }

        try
        {
            var sid = ConsoleUserSid.Resolve(HkuUninstallSubPath);
            if (sid is not null)
            {
                using var key = Registry.Users.OpenSubKey($@"{sid}\{HkuUninstallKeyPath}");
                Fold(key);
            }
        }
        catch { /* per-check swallow */ }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(HklmUninstall64Path);
            Fold(key);
        }
        catch { /* per-check swallow */ }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(HklmUninstall32Path);
            Fold(key);
        }
        catch { /* per-check swallow */ }

        return (found, live, version, installLocation);
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

    private static string? ResolveExistingProfileFile(string relativePath)
    {
        try
        {
            foreach (var profileDir in Nexus2ProfileDirs.Candidates())
            {
                var candidate = Path.Combine(profileDir, relativePath);
                if (SafeFileExists(candidate) && SafeFileLength(candidate) > 0)
                {
                    return candidate;
                }
            }
        }
        catch { /* per-check swallow */ }
        return null;
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

    public Task<bool> CloseAppAsync() => Task.FromResult(false);
}
#endif
