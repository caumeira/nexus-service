using Nexus.Service.Activity;
using Nexus.Service.Auth;
using Nexus.Service.Fps;
using Nexus.Service.Lifecycle;
using Nexus.Service.Models;
using Nexus.Service.Models.Activity;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Monitoring;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;
using Microsoft.AspNetCore.Mvc;

namespace Nexus.Service.Routes;

public static class SystemRoutes
{
    public static void MapSystemEndpoints(this WebApplication app)
    {
        app.MapPost("/system/polling-rate", (SetPollingRateBody body, [FromServices] ProcessMonitor pm, [FromServices] INetworkProvider net, [FromServices] MonitoringBroadcaster monitoring) =>
        {
            pm.SetInterval(body.PollingRate);
            net.SetInterval(body.PollingRate);
            monitoring.SetInterval(body.PollingRate);
            return ApiResponse.Ok();
        });

        app.MapGet("/system/elevation", () =>
        {
            var platform = OperatingSystem.IsWindows() ? "windows"
                : OperatingSystem.IsMacOS() ? "macos"
                : "linux";
            var elevation = ProcessElevation.GetCurrent();
            return new ProcessElevationResponse
            {
                Platform = platform,
                Supported = elevation.Supported,
                IsElevated = elevation.IsElevated,
                Status = elevation.Status,
            };
        }).AllowPanel();

        app.MapPost("/system/elevation/relaunch", () =>
        {
            var result = ProcessRelauncher.TryRelaunchAsAdmin();
            return new ProcessElevationRelaunchResponse
            {
                Result = result switch
                {
                    RelaunchResult.Started => "started",
                    RelaunchResult.AlreadyElevated => "already-elevated",
                    RelaunchResult.Unsupported => "unsupported",
                    RelaunchResult.UserDenied => "user-denied",
                    _ => "failed",
                },
            };
        });

        // No REST sensor endpoints — all hardware sensor / model data is
        // delivered via the `/monitoring` topic over the multiplex WebSocket.
        // RAM capacity ships as `theoreticalMaximum` on the Memory Used sensor.

        // Compact, shareable rig identity for the Devices → System Specs tab.
        // Cached for the lifetime of the service (hardware specs don't change
        // at runtime); `SystemSpecsPrewarmService` populates the cache off
        // the boot critical path so the first request is in-memory.
        // Async so the first post-boot request waits for LHM's background open
        // to finish (~1-3 s) and returns fully-populated CPU/motherboard/GPU
        // names. Subsequent calls hit the cache and return in microseconds.
        app.MapGet("/system/specs", (SystemSpecsCollector collector, HttpContext ctx) =>
            collector.GetAsync(ctx.RequestAborted)).AllowPanel();

        // Volume (default render endpoint)
        app.MapGet("/system/volume", (IVolumeProvider v) => v.GetState()).AllowPanel();
        app.MapPost("/system/volume", (SetVolumeBody body, IVolumeProvider v) =>
        {
            v.SetVolume(body.Volume);
            return ApiResponse.Ok();
        }).AllowPanel();
        app.MapPost("/system/volume/mute", (SetMutedBody body, IVolumeProvider v) =>
        {
            v.SetMuted(body.Muted);
            return ApiResponse.Ok();
        }).AllowPanel();
    }
}
