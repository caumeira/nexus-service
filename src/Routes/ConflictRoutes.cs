using System;
using Nexus.Service.Activity;
using Nexus.Service.Conflicts;
using Nexus.Service.Models;
using Nexus.Service.Models.Conflicts;
using Nexus.Service.Platform.Windows;

namespace Nexus.Service.Routes;

/// <summary>
/// REST surface for the sidebar conflict warning. The watcher running
/// in the background broadcasts changes over the multiplex hub on topic
/// <c>conflicts</c>; these endpoints exist for clients that prefer a
/// one-shot fetch and for the "End task" button.
/// </summary>
public static class ConflictRoutes
{
    public static void MapConflictEndpoints(this WebApplication app)
    {
        // Current set of detected conflicts. Cheap - backed by the
        // watcher's in-memory snapshot, no rescan.
        app.MapGet("/conflicts", (ConflictWatcher watcher) =>
        {
            var conflicts = watcher.GetConflicts();
            var response = new GetConflictsResponse();
            foreach (var c in conflicts)
                response.Conflicts.Add(c);
            return Results.Ok(response);
        });

        // Terminate every running process matching the catalog entry for
        // <c>body.Id</c>, then stop any Windows services it lists (for apps
        // whose background service re-grabs the hardware). We never trust a
        // caller-supplied process/service name - the SPA only sends a catalog
        // id, and we resolve it to the names we have already vetted in
        // ConflictAppCatalog.
        app.MapPost("/conflicts/kill", (KillConflictBody body) =>
        {
            var def = ConflictWatcher.FindById(body?.Id ?? "");
            if (def is null)
            {
                return Results.BadRequest(new KillConflictResponse
                {
                    Error = true,
                    Msg = "unknown conflict id",
                });
            }

            // Measured, not inferred: ProcessKiller.Kill reports that it FOUND
            // processes (its own Kill call is swallowed), and StopService counts
            // an already-stopped service as success - so neither says anything
            // stopped running. The client keeps a spinner up until Killed goes
            // false or the row clears, so a wrong true spins forever.
            var runningBefore = AnyProcessRunning(def);

            foreach (var name in def.ProcessNames)
            {
                ProcessKiller.Kill(name);
            }

            if (OperatingSystem.IsWindows())
            {
                foreach (var svc in def.WindowsServiceNames)
                {
                    WindowsServiceController.StopService(svc);
                }
            }

            var runningAfter = AnyProcessRunning(def);
            var killed = runningBefore && !runningAfter;

            return Results.Ok(new KillConflictResponse
            {
                Error = false,
                Msg = !runningBefore ? "No matching process"
                    : runningAfter ? "Still running"
                    : "Killed",
                Killed = killed,
            });
        });
    }

    /// <summary>Whether any process this catalog entry names is running; the oracle for whether a kill did anything.</summary>
    private static bool AnyProcessRunning(ConflictAppDefinition def)
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
