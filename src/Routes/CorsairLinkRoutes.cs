using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Models;
using Nexus.Service.Peripherals.CorsairLink;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    private static void MapCorsairEndpoints(WebApplication app)
    {
        // GET /devices/corsair/state - connection, firmware, the auto-detected
        // device topology with live RPM/temperature, and the iCUE-takeover flag.
        app.MapGet("/devices/corsair/state", (CorsairLinkHub hub, IConfigStore store) =>
        {
            var s = store.Load();
            var devices = new List<CorsairDeviceDto>();
            foreach (var d in hub.State.Devices)
            {
                devices.Add(new CorsairDeviceDto
                {
                    Channel = d.Channel,
                    Name = d.Name,
                    DeviceClass = d.Class.ToString(),
                    LedCount = d.LedCount,
                    HasSpeed = d.HasSpeed,
                    HasTemperature = d.HasTemperature,
                    Rpm = d.Rpm,
                    TempC = float.IsNaN(d.TempC) ? null : d.TempC,
                });
            }
            return Results.Json(new CorsairStateResponse
            {
                IsConnected = hub.IsConnected,
                Firmware = hub.State.Firmware,
                StopConflictingApps = s.Devices.Corsair.StopConflictingApps,
                Devices = devices.ToArray(),
            }, AppJsonContext.Default.CorsairStateResponse);
        });

        // PUT /devices/corsair/settings - toggle whether Nexus stops Corsair iCUE
        // when it takes over the hub.
        app.MapPut("/devices/corsair/settings", (CorsairSettingsRequest body, IConfigStore store) =>
        {
            store.Update(s =>
            {
                if (body.StopConflictingApps.HasValue)
                {
                    s.Devices.Corsair.StopConflictingApps = body.StopConflictingApps.Value;
                }
            });
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });
    }
}

public sealed class CorsairStateResponse
{
    public bool IsConnected { get; set; }
    public string Firmware { get; set; } = "";
    public bool StopConflictingApps { get; set; }
    public CorsairDeviceDto[] Devices { get; set; } = Array.Empty<CorsairDeviceDto>();
}

public sealed class CorsairDeviceDto
{
    public int Channel { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Fan | Aio | Pump | CpuBlock | GpuBlock | Case | Adapter | Other.</summary>
    public string DeviceClass { get; set; } = "";
    public int LedCount { get; set; }
    public bool HasSpeed { get; set; }
    public bool HasTemperature { get; set; }
    public int Rpm { get; set; }
    public float? TempC { get; set; }
}

public sealed class CorsairSettingsRequest
{
    public bool? StopConflictingApps { get; set; }
}
