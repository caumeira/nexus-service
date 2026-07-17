using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;

namespace Nexus.Service.Activity;

/// <summary>
/// Cross-platform utility for killing processes by name or PID.
/// Used by both the stub and real activity providers to implement
/// the <see cref="IAppDetectionProvider.Kill"/> contract.
/// </summary>
public static class ProcessKiller
{
    /// <summary>
    /// Kills all processes matching the given name.
    /// Returns true if at least one matching process was found (and kill was attempted).
    /// </summary>
    public static bool Kill(string processName)
    {
        try
        {
            var processes = Process.GetProcessesByName(processName);
            if (processes.Length == 0)
            {
                return false;
            }

            foreach (var proc in processes)
            {
                try
                { proc.Kill(true); proc.WaitForExit(3000); }
                catch { }
                finally { proc.Dispose(); }
            }
            return true;
        }
        catch { return false; }
    }

    // Shared across the whole batch, not per instance: a multi-process app
    // (browser/Electron) killing N instances must not accumulate N waits -
    // the caller's RPC timeout (ProcessActionsDomain.cs's process.kill,
    // 8000ms) bounds the whole call, and this budget must comfortably fit
    // inside it regardless of instance count.
    private const int TotalExitWaitBudgetMs = 3000;

    /// <summary>
    /// Kills every process matching processName, returning how many
    /// succeeded vs failed (e.g. access denied) rather than a single bool -
    /// used where the caller must report an exact outcome count. A tree-kill
    /// (Kill(true)) can take a sibling down before this loop reaches it;
    /// that sibling's own Kill call then throws InvalidOperationException
    /// (already exited), which counts as success, not failure.
    /// </summary>
    public static (int Killed, int Failed) KillAllCounted(string processName)
    {
        Process[] processes;
        try { processes = Process.GetProcessesByName(processName); }
        catch { return (0, 0); }

        return KillAllCounted(processes);
    }

    /// <summary>Test seam: runs the same kill/count/wait logic over an
    /// already-obtained process list, so a test can hand in handles for
    /// processes it controls directly instead of matching by name.</summary>
    internal static (int Killed, int Failed) KillAllCounted(IReadOnlyList<Process> processes)
    {
        var killed = 0;
        var failed = 0;
        var exiting = new List<Process>();
        foreach (var proc in processes)
        {
            try
            {
                proc.Kill(true);
                killed++;
                exiting.Add(proc);
            }
            catch (InvalidOperationException)
            {
                killed++;
                proc.Dispose();
            }
            catch (Win32Exception)
            {
                failed++;
                proc.Dispose();
            }
            catch
            {
                failed++;
                proc.Dispose();
            }
        }

        var stopwatch = Stopwatch.StartNew();
        foreach (var proc in exiting)
        {
            var remainingMs = TotalExitWaitBudgetMs - (int)stopwatch.ElapsedMilliseconds;
            try
            {
                if (remainingMs > 0)
                {
                    proc.WaitForExit(remainingMs);
                }
            }
            catch { }
            finally { proc.Dispose(); }
        }

        return (killed, failed);
    }

    /// <summary>
    /// Kills a single process by its PID.
    /// Returns true if the process was found and killed successfully.
    /// </summary>
    public static bool KillByPid(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            proc.Kill(true);
            proc.WaitForExit(3000);
            return true;
        }
        catch { return false; }
    }
}
