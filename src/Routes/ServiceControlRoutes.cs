using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Auth;
using Nexus.Service.Models;

namespace Nexus.Service.Routes;

/// <summary>
/// Service-control surface. Four operations:
///   GET  /service/startup-mode  -> current SCM start type (auto/demand)
///   POST /service/startup-mode  -> change SCM start type for next boot
///   POST /service/stop          -> graceful self-stop
///   POST /service/open-app      -> ensure dashboard is open
///
/// All are protected by two stacked gates:
///   1. <see cref="LocalhostOnlyEndpointExtensions.LocalhostOnly"/> - the
///      auth middleware 404s any non-loopback caller before token checks
///      even fire, so the route's existence isn't leaked to LAN scanners.
///   2. The standard token-auth middleware that already covers every
///      non-SPA-fallback route. Loopback callers without a valid dashboard
///      token still get 401. This neutralizes DNS-rebind attacks too: a
///      malicious cross-origin page can't read the legit dashboard's
///      token, so its request hits 401 even after rebinding to 127.0.0.1.
/// None of these routes call <c>.AllowPanel()</c>, so panel-session
/// tokens (phones, Y70 displays) can't reach them either.
/// </summary>
internal static class ServiceControlRoutes
{
    public static void MapServiceControlEndpoints(this WebApplication app)
    {
        app.MapGet("/service/startup-mode", () => Results.Ok(new StartupModeDto
        {
            AutoStart = ReadAutoStart(),
        })).LocalhostOnly();

        app.MapPost("/service/startup-mode", (StartupModeBody body) =>
        {
            var ok = WriteAutoStart(body.AutoStart);
            return ok ? Results.Ok(new StartupModeDto { AutoStart = ReadAutoStart() })
                      : Results.Problem("sc.exe config failed", statusCode: 500);
        }).LocalhostOnly();

        app.MapPost("/service/stop", (IHostApplicationLifetime lifetime,
            Nexus.Service.Devices.Firmware.FirmwareFlasher flasher) =>
        {
            // Never tear the service down mid-flash — that would strand the
            // device in the DFU bootloader. Refuse the stop while a firmware
            // update is running; the UI also blocks its quit affordance.
            if (flasher.IsFlashing)
            {
                return Results.Json(
                    new ApiResponse { Error = true, Msg = "A firmware update is in progress; cannot stop the service." },
                    Nexus.Service.Serialization.AppJsonContext.Default.ApiResponse,
                    statusCode: 409);
            }

            // Fire-and-forget so the response can flush before the host
            // tears down. Lifetime.StopApplication signals the web host's
            // ApplicationStopping token, which is what our SCM dispatcher
            // (WindowsServiceHost) is waiting on - SCM then sees the
            // service transition to STOPPED.
            _ = Task.Run(async () =>
            {
                await Task.Delay(200);
                lifetime.StopApplication();
            });
            return Results.Ok(ApiResponse.Ok());
        }).LocalhostOnly();

        app.MapPost("/service/open-app", () =>
        {
#if WINDOWS
            // The service runs as LocalSystem in Session 0 - spawning Edge
            // --app from here would land in a non-interactive session and
            // never show. Delegate to a one-shot Nexus.exe --open-app in the
            // active console session (same schtasks hop the helper bootstrap
            // uses).
            Nexus.Service.Lifecycle.UserHelperBootstrapper.LaunchOpenApp();
#else
            if (OperatingSystem.IsMacOS())
            {
                Nexus.Service.Platform.Mac.MacAppWindow.OpenOrFocus(Nexus.Service.Platform.ServiceLaunchIntent.LocalDashboardUrl(0));
            }
#endif
            return Results.Ok(ApiResponse.Ok());
        }).LocalhostOnly();
    }

    private const string ServiceName = "NexusService";

    private static bool ReadAutoStart()
    {
        var (code, output) = RunSc("qc", ServiceName);
        if (code != 0) return false;
        // sc qc emits a START_TYPE line, e.g. "  START_TYPE  : 2 AUTO_START".
        return output.Contains("AUTO_START", StringComparison.OrdinalIgnoreCase);
    }

    private static bool WriteAutoStart(bool enable)
    {
        var startMode = enable ? "auto" : "demand";
        var (code, _) = RunSc("config", ServiceName, $"start=", startMode);
        return code == 0;
    }

    private static (int code, string output) RunSc(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            return (p.ExitCode, stdout);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[service-control] sc.exe failed: {ex.Message}");
            return (-1, string.Empty);
        }
    }

}

public sealed class StartupModeBody
{
    public bool AutoStart { get; set; }
}

public sealed class StartupModeDto
{
    public bool AutoStart { get; set; }
}
