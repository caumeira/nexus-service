using Nexus.Service.Conflicts;
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

        // Every app the catalog knows about, running or not - what the
        // settings modal lists so a user can opt an app out of the startup
        // shutdown before it has ever been detected. Names the app only;
        // the process/service names it resolves to stay server-side.
        app.MapGet("/conflicts/catalog", () =>
        {
            var response = new GetConflictCatalogResponse();
            foreach (var def in ConflictAppCatalog.All)
            {
                response.Apps.Add(new ConflictCatalogApp
                {
                    Id = def.Id,
                    DisplayName = def.DisplayName,
                    Category = def.Category,
                });
            }
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
            // processes, its own Kill being swallowed, and StopService counts an
            // already-stopped service as success.
            var outcome = ConflictKiller.Kill(def);

            return Results.Ok(new KillConflictResponse
            {
                Error = false,
                Msg = !outcome.RunningBefore ? "No matching process"
                    : outcome.RunningAfter ? "Still running"
                    : "Killed",
                Killed = outcome.Killed,
            });
        });
    }
}
