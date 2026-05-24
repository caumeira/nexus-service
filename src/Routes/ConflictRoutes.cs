using Nexus.Service.Activity;
using Nexus.Service.Conflicts;
using Nexus.Service.Models;
using Nexus.Service.Models.Conflicts;

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
        // Current set of detected conflicts. Cheap — backed by the
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
        // <c>body.Id</c>. We never trust a caller-supplied process name —
        // the SPA only sends a catalog id, and we resolve it to the names
        // we have already vetted in ConflictAppCatalog.
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

            return Results.Ok(new KillConflictResponse
            {
                Error = false,
                Msg = killed ? "Killed" : "No matching process",
                Killed = killed,
            });
        });
    }
}
