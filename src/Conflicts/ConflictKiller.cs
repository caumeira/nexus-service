using System;
using Nexus.Service.Activity;
using Nexus.Service.Platform.Windows;

namespace Nexus.Service.Conflicts;

/// <summary>
/// Terminates a competing app named by a <see cref="ConflictAppDefinition"/>.
/// Shared by the "End task" endpoint and the opt-in startup shutdown so both
/// paths use the same vetted process/service names - a caller never supplies
/// a process name, only a catalog id.
/// </summary>
public static class ConflictKiller
{
    /// <summary>Outcome of a kill attempt. <paramref name="Killed"/> is measured, not inferred: the app was running before and is not running after.</summary>
    public readonly record struct KillOutcome(bool RunningBefore, bool RunningAfter)
    {
        public bool Killed => RunningBefore && !RunningAfter;
    }

    public static KillOutcome Kill(ConflictAppDefinition def)
    {
        var runningBefore = AnyProcessRunning(def);

        // Services first: these entries exist because the service restarts
        // the app's processes, so killing those first lets a live service
        // relaunch them inside ProcessKiller's exit wait. Stopping first
        // also means the process pass reaps a host still in STOP_PENDING.
        if (OperatingSystem.IsWindows())
        {
            foreach (var svc in def.WindowsServiceNames)
            {
                WindowsServiceController.StopService(svc);
            }
        }

        foreach (var name in def.ProcessNames)
        {
            ProcessKiller.Kill(name);
        }

        return new KillOutcome(runningBefore, AnyProcessRunning(def));
    }

    /// <summary>Whether any process this catalog entry names is running; the oracle for whether a kill did anything.</summary>
    public static bool AnyProcessRunning(ConflictAppDefinition def)
    {
        foreach (var name in def.ProcessNames)
        {
            try
            {
                var procs = System.Diagnostics.Process.GetProcessesByName(name);
                foreach (var p in procs) p.Dispose();
                if (procs.Length > 0) return true;
            }
            catch { /* an unreadable process list is not evidence of absence */ }
        }
        return false;
    }
}
