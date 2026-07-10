using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Peripherals.CorsairLink;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    private static void MapCorsairEndpoints(WebApplication app)
    {
        // GET /devices/corsair/state - connection, firmware, and the
        // auto-detected device topology with live RPM/temperature.
        app.MapGet("/devices/corsair/state", (CorsairLinkHub hub) =>
        {
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
                    Serial = d.Serial,
                });
            }
            return Results.Json(new CorsairStateResponse
            {
                IsConnected = hub.IsConnected,
                Firmware = hub.State.Firmware,
                Devices = devices.ToArray(),
            }, AppJsonContext.Default.CorsairStateResponse);
        });
    }
}

public sealed class CorsairStateResponse
{
    public bool IsConnected { get; set; }
    public string Firmware { get; set; } = "";
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
    /// <summary>Hub-assigned device serial; disambiguates otherwise-identical fans.</summary>
    public string Serial { get; set; } = "";
}
