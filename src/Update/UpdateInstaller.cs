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
/// Task-name format: NexusOtaInstall_{pid}_{ticks}. The task is not deleted
/// synchronously (the service is about to die); orphaned NexusOtaInstall_*
/// tasks are cleaned up on the next service boot.
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

#if WINDOWS
    private const string TaskPrefix = "NexusOtaInstall";

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

        // Delete any pre-existing log so the SYSTEM installer creates it fresh in
        // the locked dir: File truncate keeps a planted file's owner/DACL (and a
        // planted symlink would redirect the SYSTEM write).
        var logPath = Path.Combine(UpdateDownloader.StagingDir, $"ota-install-{version}.log");
        TryDeleteForReplace(logPath);
        var taskName = $"{TaskPrefix}_{Environment.ProcessId}_{DateTime.UtcNow.Ticks}";

        // Build the launcher .cmd file to avoid /TR quoting hell with long paths.
        // Delete-then-create (never truncate-in-place): File truncate preserves a
        // pre-planted file's owner + DACL, leaving the .cmd that SYSTEM executes
        // attacker-writable for an overwrite race. A fresh file in the locked dir
        // inherits its SYSTEM-only ACL. Abort if a stale one can't be removed.
        var cmdPath = Path.Combine(UpdateDownloader.StagingDir, $"run-ota-{version}.cmd");
        if (!TryDeleteForReplace(cmdPath))
        {
            Console.Error.WriteLine($"[ota-install] could not replace stale launcher: {cmdPath}");
            return false;
        }
        File.WriteAllText(cmdPath,
            $"@echo off\r\n\"{installerPath}\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART \"/LOG={logPath}\"\r\n");

        if (!Schtasks("/Create",
                      "/TN", taskName,
                      "/TR", $"\"{cmdPath}\"",
                      "/SC", "ONCE",
                      "/ST", "23:59",
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

                if (!taskName.StartsWith(TaskPrefix, StringComparison.OrdinalIgnoreCase)) continue;
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
