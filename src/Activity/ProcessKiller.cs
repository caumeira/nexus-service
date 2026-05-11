using System;
using System.Diagnostics;

namespace Qos.Service.Activity;

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
