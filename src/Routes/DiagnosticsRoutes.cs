using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
#if WINDOWS
using Microsoft.Extensions.DependencyInjection;
#endif
using Nexus.Service.Auth;
using Nexus.Service.Models;

namespace Nexus.Service.Routes;

/// <summary>
/// Tester-facing diagnostics surface. <c>/diagnostics/open-logs</c> reveals the
/// Nexus logs folder (nexus-service.log, plus desktop-host.log on Windows) in the OS
/// file manager so a tester can attach them to a bug report. Loopback-only: it
/// acts on the local machine, so a paired phone or LAN caller has no business
/// reaching it.
/// </summary>
internal static class DiagnosticsRoutes
{
    public static void MapDiagnosticsEndpoints(this WebApplication app)
    {
        app.MapPost("/diagnostics/open-logs", (IServiceProvider sp) =>
        {
            try
            {
#if WINDOWS
                // The service is LocalSystem in Session 0; an explorer.exe it
                // spawns lands in the non-interactive session and never shows.
                // Hand off to the user-session helper over the pipe - it opens
                // the folder on the user's desktop.
                var registry = sp.GetRequiredService<Nexus.Service.Helper.HelperRegistry>();
                _ = Nexus.Service.Helper.Domains.DiagnosticsCommands.OpenLogsAsync(registry);
#else
                // macOS (LaunchAgent) / Linux run in the user session already,
                // so opening directly reaches the user's file manager.
                Nexus.Service.Diagnostics.LogsFolder.Open();
#endif
                return Results.Ok(ApiResponse.Ok());
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[diagnostics] open-logs failed: {ex.Message}");
                return Results.Problem(ex.Message);
            }
        }).LocalhostOnly();
    }
}
