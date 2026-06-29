using Microsoft.AspNetCore.Http;
using Nexus.Service.Peripherals.Galahad2;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    private static void MapGalahad2Endpoints(WebApplication app)
    {
        // GET /devices/lianli-aio/state - connected flag, fan and pump RPM and duty.
        app.MapGet("/devices/lianli-aio/state", (Galahad2Hub hub) =>
        {
            // Read snapshot once; all field accesses use this single reference.
            var snap = hub.Snapshot;
            return Results.Json(
                new Galahad2StateResponse
                {
                    IsConnected = hub.IsConnected,
                    FanRpm = snap.FanRpm,
                    PumpRpm = snap.PumpRpm,
                    FanDuty = snap.FanDuty,
                    PumpDuty = snap.PumpDuty,
                },
                AppJsonContext.Default.Galahad2StateResponse);
        });
    }
}

public sealed class Galahad2StateResponse
{
    public bool IsConnected { get; set; }
    public int FanRpm { get; set; }
    public int PumpRpm { get; set; }
    public int FanDuty { get; set; }
    public int PumpDuty { get; set; }
}
