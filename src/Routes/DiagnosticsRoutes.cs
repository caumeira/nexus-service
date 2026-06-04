using System;
using System.Diagnostics;
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Auth;
using Nexus.Service.Models;
using Nexus.Service.Platform;

namespace Nexus.Service.Routes;

/// <summary>
/// Tester-facing diagnostics surface. <c>/diagnostics/open-logs</c> reveals the
/// Nexus logs folder (service.log, plus desktop-host.log on Windows) in the OS
/// file manager so a tester can attach them to a bug report. Loopback-only: it
/// acts on the local machine, so a paired phone or LAN caller has no business
/// reaching it.
/// </summary>
internal static class DiagnosticsRoutes
{
    public static void MapDiagnosticsEndpoints(this WebApplication app)
    {
        app.MapPost("/diagnostics/open-logs", () =>
        {
            try
            {
                var dir = ServiceLog.LogsDirectory;
                Directory.CreateDirectory(dir);
                var psi = new ProcessStartInfo { UseShellExecute = true };
                if (OperatingSystem.IsWindows())
                { psi.FileName = "explorer.exe"; psi.Arguments = $"\"{dir}\""; }
                else if (OperatingSystem.IsMacOS())
                { psi.FileName = "open"; psi.Arguments = $"\"{dir}\""; psi.UseShellExecute = false; }
                else
                { psi.FileName = "xdg-open"; psi.Arguments = $"\"{dir}\""; psi.UseShellExecute = false; }
                Process.Start(psi);
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
