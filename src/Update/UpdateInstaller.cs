using System;
using System.Diagnostics;
using System.IO;
#if WINDOWS
using Nexus.Service.Lifecycle;
#endif

namespace Nexus.Service.Update;

/// <summary>
/// Hands off a verified installer to Windows Task Scheduler running as SYSTEM
/// and returns. The installer runs detached under Task Scheduler so it survives
/// StopApplication and its own taskkill of Nexus.exe. The caller stops the
/// service after this returns true.
///
/// The task carries an already-past ONCE trigger and is disabled right after
/// the one-shot schtasks /Run, so Task Scheduler never auto-runs it on a wall
/// clock - it fires exactly once, from that /Run (a start-up auto-apply or the
/// user's install button). Task-name format: NexusOtaInstall_{pid}_{ticks}; the
/// spent task is not deleted synchronously (the service is about to die),
/// CleanOrphanedTasks removes it on the next boot.
/// </summary>
public static class UpdateInstaller
{
    // Platform-agnostic so it is unit-tested off-Windows; the install path that
    // consumes it is Windows-only below.
    internal static bool IsValidVersionTag(string v)
    {
        // Accept a "v"-prefixed semver tag: a dotted-numeric core ("v3.0.1",
        // legacy "v80") plus an optional "-beta.N" / "-rc.N" prerelease suffix
        // ("v3.0.1-beta.1"). The charset is limited to digits, ASCII letters,
        // '.', and '-' - none are path separators, so the version stays safe to
        // embed in the staged .cmd / log file paths and the schtask name.
        if (string.IsNullOrEmpty(v) || v[0] != 'v' || v.Length < 2)
        {
            return false;
        }

        bool hasDigit = false;
        for (int i = 1; i < v.Length; i++)
        {
            char c = v[i];
            if (char.IsAsciiDigit(c)) { hasDigit = true; }
            else if (c != '.' && c != '-' && !char.IsAsciiLetter(c)) { return false; }
        }

        return hasDigit;
    }

    // Platform-agnostic so it is unit-tested off-Windows; the launch that
    // consumes it is Windows-only below.
    internal static bool IsWithinStagingDir(string installerPath)
    {
        if (string.IsNullOrEmpty(installerPath))
        {
            return false;
        }

        try
        {
            var staging = Path.GetFullPath(UpdateDownloader.StagingDir);
            var prefix = staging.EndsWith(Path.DirectorySeparatorChar)
                ? staging
                : staging + Path.DirectorySeparatorChar;
            return Path.GetFullPath(installerPath).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // Scheduled-task name prefix for the detached installer launch. Kept
    // platform-agnostic (with the matcher below) so the cleanup logic is
    // unit-tested off-Windows.
    internal const string TaskPrefix = "NexusOtaInstall";

    // Filename prefix + glob for the per-attempt Inno logs in the staging dir.
    internal const string InstallLogPrefix = "ota-install-";
    internal const string InstallLogGlob = InstallLogPrefix + "*.log";

    // Filename prefix + glob for the per-version launcher .cmd in the staging dir.
    internal const string LauncherPrefix = "run-ota-";
    internal const string LauncherGlob = LauncherPrefix + "*.cmd";

    // How many per-attempt install logs to keep. Enough to cover the
    // failed-then-retried pattern several times over without letting the
    // staging dir grow without bound.
    internal const int KeepInstallLogs = 10;

    // Platform-agnostic so it is unit-tested off-Windows.
    internal static string InstallLogName(string version, long stamp) =>
        $"{InstallLogPrefix}{version}-{stamp}.log";

    // Platform-agnostic so it is unit-tested off-Windows.
    internal static string LauncherName(string version) =>
        $"{LauncherPrefix}{version}.cmd";

    /// <summary>
    /// Trims the completed per-attempt install logs in <paramref name="dir"/> to
    /// the newest <see cref="KeepInstallLogs"/>; the in-flight attempt's log is
    /// created afterwards, so the directory settles one above that. Best-effort:
    /// a log that will not delete is left alone rather than failing the install
    /// that is about to run.
    /// </summary>
    internal static void PruneOldInstallLogs(string dir)
    {
        try
        {
            var logs = Directory.GetFiles(dir, InstallLogGlob);
            if (logs.Length <= KeepInstallLogs)
            {
                return;
            }

            // Stamp each path once: re-reading the timestamp inside the
            // comparison makes it inconsistent if a write lands mid-sort, and
            // Array.Sort raises InvalidOperationException on that.
            var byAge = new (string Path, DateTime Written)[logs.Length];
            for (var i = 0; i < logs.Length; i++)
            {
                byAge[i] = (logs[i], File.GetLastWriteTimeUtc(logs[i]));
            }

            Array.Sort(byAge, (a, b) => b.Written.CompareTo(a.Written));
            for (var i = KeepInstallLogs; i < byAge.Length; i++)
            {
                try { File.Delete(byAge[i].Path); } catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// Deletes every launcher in <paramref name="dir"/> except
    /// <paramref name="keepFileName"/>. A launcher names one installer and
    /// nothing else, so every other version's is dead weight once its installer
    /// has been superseded. Runs here rather than alongside the installer prune
    /// in <see cref="UpdateDownloader.PruneStaleInstallers"/> because only this
    /// call site knows which launcher is live: schtasks /Run returns as soon as
    /// the task is TRIGGERED, so a prune driven by a later download could delete
    /// the .cmd of an install Task Scheduler has not opened yet. Install logs are
    /// kept - they are the only record of a failed attempt.
    /// </summary>
    internal static void PruneStaleLaunchers(string dir, string keepFileName)
    {
        try
        {
            foreach (var f in Directory.GetFiles(dir, LauncherGlob))
            {
                if (!string.Equals(Path.GetFileName(f), keepFileName, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(f); } catch { }
                }
            }
        }
        catch { }
    }

    // True when a `schtasks /Query /FO CSV` task-name field is one of ours. That
    // field is the full task path with a leading '\' (e.g. "\NexusOtaInstall_1"),
    // so match the leaf - a raw StartsWith is defeated by the backslash and
    // orphaned tasks then never get cleaned.
    internal static bool IsOwnedTaskName(string csvTaskName)
    {
        if (string.IsNullOrEmpty(csvTaskName)) return false;
        var leaf = csvTaskName[(csvTaskName.LastIndexOf('\\') + 1)..];
        return leaf.StartsWith(TaskPrefix, StringComparison.OrdinalIgnoreCase);
    }

#if WINDOWS
    /// <summary>
    /// Schedules <paramref name="installerPath"/> to run as SYSTEM at HIGHEST
    /// privilege and triggers it immediately. Returns true when the task was
    /// created and triggered successfully.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static bool LaunchViaSchtasks(string installerPath, string version)
    {
        if (!IsValidVersionTag(version))
        {
            Console.Error.WriteLine($"[ota-install] rejected malformed version tag: {version}");
            return false;
        }

        // The installer runs as SYSTEM, so its path must resolve inside the
        // SYSTEM-locked staging dir - never an attacker-chosen location a marker
        // could point at.
        if (!IsWithinStagingDir(installerPath))
        {
            Console.Error.WriteLine($"[ota-install] rejected installer path outside staging dir: {installerPath}");
            return false;
        }

        // Re-assert the SYSTEM-only ACL before writing the .cmd that runs as
        // SYSTEM, closing any window where a non-admin could tamper with it.
        UpdateDownloader.EnsureSecureStagingDir();

        if (!File.Exists(installerPath))
        {
            Console.Error.WriteLine($"[ota-install] installer not found: {installerPath}");
            return false;
        }

        // One log per ATTEMPT: two attempts at the same version are routine (a
        // startup auto-apply that fails, then the user's retry), and both
        // diagnoses have to survive. The stamp is shared with the task name so a
        // log pairs with its "[ota-install] scheduled task ..." line.
        var stamp = DateTime.UtcNow.Ticks;
        var taskName = $"{TaskPrefix}_{Environment.ProcessId}_{stamp}";

        // Delete any pre-existing log so the SYSTEM installer creates it fresh in
        // the locked dir: File truncate keeps a planted file's owner/DACL (and a
        // planted symlink would redirect the SYSTEM write).
        var logPath = Path.Combine(UpdateDownloader.StagingDir, InstallLogName(version, stamp));
        TryDeleteForReplace(logPath);
        PruneOldInstallLogs(UpdateDownloader.StagingDir);

        // Build the launcher .cmd file to avoid /TR quoting hell with long paths.
        // Delete-then-create (never truncate-in-place): File truncate preserves a
        // pre-planted file's owner + DACL, leaving the .cmd that SYSTEM executes
        // attacker-writable for an overwrite race. A fresh file in the locked dir
        // inherits its SYSTEM-only ACL. Abort if a stale one can't be removed.
        var cmdPath = Path.Combine(UpdateDownloader.StagingDir, LauncherName(version));
        if (!TryDeleteForReplace(cmdPath))
        {
            Console.Error.WriteLine($"[ota-install] could not replace stale launcher: {cmdPath}");
            return false;
        }
        File.WriteAllText(cmdPath,
            $"@echo off\r\n\"{installerPath}\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART \"/LOG={logPath}\"\r\n");

        // Only after the live launcher exists: every other one belongs to a
        // superseded installer the downloader has already pruned.
        PruneStaleLaunchers(UpdateDownloader.StagingDir, Path.GetFileName(cmdPath));

        // /ST 00:00 with the default (today) start date leaves the ONCE trigger
        // already in the past, so Task Scheduler never auto-runs it - the install
        // only ever happens from the /Run below. /ST is a locale-independent
        // HH:mm, so this needs no /SD date string (which parses in system locale).
        if (!Schtasks("/Create",
                      "/TN", taskName,
                      "/TR", $"\"{cmdPath}\"",
                      "/SC", "ONCE",
                      "/ST", "00:00",
                      "/RU", "SYSTEM",
                      "/RL", "HIGHEST",
                      "/F"))
        {
            Console.Error.WriteLine($"[ota-install] schtasks /Create failed for {taskName}");
            return false;
        }

        var ran = Schtasks("/Run", "/TN", taskName);
        if (!ran)
        {
            Console.Error.WriteLine($"[ota-install] schtasks /Run failed for {taskName}");
        }
        else
        {
            // Disable the spent task so no leftover trigger can fire it again on a
            // wall clock - a redundant guard for the one case the already-past /ST
            // above misses: a launch inside the 00:00 minute. Must follow /Run; a
            // disabled task will not /Run.
            Schtasks("/Change", "/TN", taskName, "/DISABLE");

            // Prevent SCM from restarting the old binary while the installer
            // swaps Nexus.exe. RunInstall re-sets the failure actions on the
            // next successful boot.
            try { WindowsServiceInstaller.SuspendFailureActionsForUpdate(); }
            catch (Exception ex) { Console.Error.WriteLine($"[ota-install] suspend failure-actions failed: {ex.Message}"); }
        }

        Console.Error.WriteLine($"[ota-install] scheduled task {taskName} triggered: {ran}");
        return ran;
    }

    /// <summary>
    /// Removes all stale NexusOtaInstall_* scheduled tasks left by a prior
    /// install attempt. Called on service startup to clean up from previous runs.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void CleanOrphanedTasks()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                Arguments = "/Query /FO CSV /NH",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                // CSV first field is the task name (quoted).
                var trimmed = line.TrimStart('"');
                var end = trimmed.IndexOf('"');
                if (end < 0) continue;
                var taskName = trimmed[..end];

                if (!IsOwnedTaskName(taskName)) continue;
                Schtasks("/Delete", "/TN", taskName, "/F");
                Console.Error.WriteLine($"[ota-install] cleaned orphaned task: {taskName}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ota-install] CleanOrphanedTasks failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes <paramref name="path"/> so the caller can recreate it fresh under
    /// the locked staging dir's ACL. Returns true when the path is absent
    /// afterward (deleted or never present), false when a file stubbornly remains.
    /// </summary>
    private static bool TryDeleteForReplace(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch { }
        return !File.Exists(path);
    }

    private static bool Schtasks(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(15_000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
#endif
}
