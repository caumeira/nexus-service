using System.Diagnostics;
using Nexus.Service.Lifecycle;
using Nexus.Service.Models;
using Nexus.Service.Models.Lifecycle;

namespace Nexus.Service.Routes;

public static class LifecycleRoutes
{
    public static void MapLifecycleEndpoints(this WebApplication app)
    {
        app.MapGet("/start", (IStartupProvider s) => new WillStartResponse { Enabled = s.IsEnabled() });
        app.MapPost("/start", (SetWillStartParams body, IStartupProvider s) =>
        {
            var path = string.IsNullOrWhiteSpace(body.Path) ? ResolveCurrentExecutablePath() : body.Path;
            var ok = s.SetEnabled(body.Enabled, path, body.Arguments);
            var enabled = s.IsEnabled();
            return new WillStartResponse { Enabled = enabled, Error = !ok || enabled != body.Enabled };
        });

        app.MapPost("/shutdown", (IShutdownProvider s) =>
        {
            s.Shutdown();
            return ApiResponse.Ok();
        });

        app.MapGet("/pawnio", (IPawnIoProvider p) =>
            new Models.Lifecycle.PawnIoStatus { Installed = p.IsInstalled, Open = p.IsOpen });
    }

    private static string ResolveCurrentExecutablePath()
    {
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            return Environment.ProcessPath;
        }

        try
        {
            var modulePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(modulePath))
            {
                return modulePath;
            }
        }
        catch { /* best-effort fallback below */ }

        return Path.Combine(AppContext.BaseDirectory, "Nexus.exe");
    }
}
