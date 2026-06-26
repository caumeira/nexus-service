using System;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Lighting;
using Nexus.Service.Models;
using Nexus.Service.Peripherals.LianLi;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    private static void MapLianLiEndpoints(WebApplication app)
    {
        // GET /devices/lianli/state - connection, RPM, duty, and fan count per port.
        app.MapGet("/devices/lianli/state", (LianLiHub hub, IConfigStore store) =>
        {
            var s = store.Load();
            var state = hub.State;
            var resp = new LianLiStateResponse
            {
                IsConnected = hub.IsConnected,
                Rpm = new[] { state.Rpm[0], state.Rpm[1], state.Rpm[2], state.Rpm[3] },
                Duty = new[] { state.Duty[0], state.Duty[1], state.Duty[2], state.Duty[3] },
                FansPerPort = new[]
                {
                    s.Devices.LianLi.Port0Fans,
                    s.Devices.LianLi.Port1Fans,
                    s.Devices.LianLi.Port2Fans,
                    s.Devices.LianLi.Port3Fans,
                },
            };
            return Results.Json(resp, AppJsonContext.Default.LianLiStateResponse);
        });

        // PUT /devices/lianli/fan-count - set fan count for one port (0..3), sends
        // SetQuantity to hub and persists the count so the lighting provider
        // allocates the correct LED frames.
        app.MapPut("/devices/lianli/fan-count", (
            LianLiFanCountRequest body,
            LianLiHub hub,
            IConfigStore store,
            LianLiLightingDeviceProvider lighting) =>
        {
            if (body.Port < 0 || body.Port >= LianLiProtocol.PortCount)
            {
                return Results.BadRequest(ApiResponse.Fail("port must be 0..3"));
            }
            if (body.Count < 0 || body.Count > 4)
            {
                return Results.BadRequest(ApiResponse.Fail("count must be 0..4"));
            }
            if (hub.IsConnected)
            {
                hub.SetQuantity(body.Port, body.Count);
            }
            store.Update(s => s.Devices.LianLi.SetFans(body.Port, body.Count));
            lighting.OnHubStateUpdated();
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });
    }
}

public sealed class LianLiStateResponse
{
    public bool IsConnected { get; set; }
    public int[] Rpm { get; set; } = Array.Empty<int>();
    public int[] Duty { get; set; } = Array.Empty<int>();
    public int[] FansPerPort { get; set; } = Array.Empty<int>();
}

public sealed class LianLiFanCountRequest
{
    public int Port { get; set; }
    public int Count { get; set; }
}
