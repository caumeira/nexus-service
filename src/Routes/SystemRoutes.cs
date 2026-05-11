using Qos.Service.Activity;
using Qos.Service.Auth;
using Qos.Service.Fps;
using Qos.Service.Lifecycle;
using Qos.Service.Models;
using Qos.Service.Models.Activity;
using Qos.Service.Models.Sensors;
using Qos.Service.Monitoring;
using Qos.Service.Platform;
using Qos.Service.Sensors;
using Microsoft.AspNetCore.Mvc;

namespace Qos.Service.Routes;

public static class SystemRoutes
{
    public static void MapSystemEndpoints(this WebApplication app)
    {
        app.MapGet("/system/os-version", (ISensorProvider s) =>
            new ApiResponse { Msg = s.GetOsVersion() }).AllowPanel();

        app.MapGet("/system/polling-rate", ([FromServices] MonitoringBroadcaster monitoring) =>
            new GetPollingRateResponse { PollingRate = monitoring.GetInterval() }).AllowPanel();

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

        // CPU
        app.MapGet("/system/cpu/sensors", (ISensorProvider s) => s.GetCpuSensors()).AllowPanel();
        app.MapGet("/system/cpu/model", (ISensorProvider s) => new GetModelResponse { Model = s.GetCpuModel() }).AllowPanel();
        app.MapGet("/system/cpu/health", (ISensorProvider s) =>
        {
            var (healthy, distance) = s.GetCpuHealth();
            return new CpuHealthResponse { Healthy = healthy, DistanceToTJMax = distance };
        }).AllowPanel();

        // GPU
        app.MapGet("/system/gpu/sensors", (ISensorProvider s) => s.GetGpuSensors()).AllowPanel();
        app.MapGet("/system/gpu/model", (ISensorProvider s) =>
            new GetGpuModelsResponse { Models = new List<string>(s.GetGpuModels()) }).AllowPanel();

        // Memory
        app.MapGet("/system/memory/sensors", (ISensorProvider s) => s.GetMemorySensors()).AllowPanel();
        app.MapGet("/system/memory/total", (ISensorProvider s) =>
            new ApiResponse { Msg = s.GetMemoryTotalFormatted() }).AllowPanel();

        // Storage
        app.MapGet("/system/storage/sensors", (ISensorProvider s) => s.GetStorageComponents()).AllowPanel();
        app.MapGet("/system/storage/partitions", (ISensorProvider s) =>
            new GetStoragePartitionsResponse { Partitions = new List<string>(s.GetStoragePartitions()) }).AllowPanel();
        app.MapGet("/system/storage/info", (ISensorProvider s) =>
            new GetDriveStorageResponse { Storage = new List<StorageDriveInfo>(s.GetStorageInfo()) }).AllowPanel();

        // Motherboard
        app.MapGet("/system/motherboard/sensors", (ISensorProvider s) => s.GetMotherboardSensors()).AllowPanel();
        app.MapGet("/system/motherboard/model", (ISensorProvider s) =>
            new GetModelResponse { Model = s.GetMotherboardModel() }).AllowPanel();

        // FPS
        app.MapGet("/system/fps/sensors", (IFpsProvider fps) => fps.GetComponent().Sensors).AllowPanel();

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
