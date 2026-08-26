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

        // Read-only: which autostart entry (if any) launches each detected
        // conflict. Separate from GET /conflicts so the watcher's poll stays
        // cheap, and so nothing is discovered until a user opens the modal.
        app.MapGet("/conflicts/autostart", (ConflictWatcher watcher) =>
        {
            var response = new GetConflictAutostartResponse();
            foreach (var c in watcher.GetConflicts())
            {
                var def = ConflictWatcher.FindById(c.Id);
                response.Apps.Add(new ConflictAutostartStatus
                {
                    Id = c.Id,
                    Autostart = def is not null && OperatingSystem.IsWindows()
                        ? ConflictAutostartLocator.Find(c, def)
                        : null,
                });
            }
            return Results.Ok(response);
        });

        // Removes one app's autostart entry. Only ever reached from an explicit
        // per-app user action; nothing here runs on detection or on render.
        app.MapPost("/conflicts/autostart/disable", (DisableConflictAutostartBody body, ConflictWatcher watcher) =>
        {
            if (!OperatingSystem.IsWindows())
            {
                return Results.Ok(new DisableConflictAutostartResponse { Ok = false, Msg = "unsupported platform" });
            }
            var id = body?.Id ?? "";
            var def = ConflictWatcher.FindById(id);
            if (def is null)
            {
                return Results.BadRequest(new DisableConflictAutostartResponse { Ok = false, Msg = "unknown conflict id" });
            }
            // Re-resolve rather than trusting a caller-supplied entry name, so a
            // request can only ever remove an entry we independently matched to
            // this app's own executable.
            ConflictAutostartEntry? entry = null;
            foreach (var c in watcher.GetConflicts())
            {
                if (string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    entry = ConflictAutostartLocator.Find(c, def);
                    break;
                }
            }
            if (entry is null)
            {
                return Results.Ok(new DisableConflictAutostartResponse { Ok = false, Msg = "no autostart entry" });
            }
            var ok = ConflictAutostartLocator.Disable(entry);
            return Results.Ok(new DisableConflictAutostartResponse { Ok = ok, Msg = ok ? "ok" : "failed" });
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

            bool killed = false;
            foreach (var name in def.ProcessNames)
            {
                if (ProcessKiller.Kill(name))
                    killed = true;
            }

            if (OperatingSystem.IsWindows())
            {
                foreach (var svc in def.WindowsServiceNames)
                {
                    if (WindowsServiceController.StopService(svc) == ServiceStopResult.Stopped)
                    {
                        killed = true;
                    }
                }
            }

            return Results.Ok(new KillConflictResponse
            {
                Error = false,
                Msg = killed ? "Killed" : "No matching process",
                Killed = killed,
            });
        });
    }
}
