using Microsoft.AspNetCore.Http;
using Nexus.Service.Models;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public sealed class Slv3MacRequest
{
    public string Mac { get; set; } = "";
}

/// <summary>
/// First-party Lian Li L-Wireless (SLV3) dongle routes: discovery,
/// bind/unbind/identify, chain reset. See plans/lianli-wireless-support.md.
/// </summary>
public static class Slv3Routes
{
    public static void MapSlv3Endpoints(this WebApplication app)
    {
        // GET /devices/lianli-wireless/state - link + fan list.
        app.MapGet("/devices/lianli-wireless/state", (Slv3Hub hub) =>
            Results.Json(hub.State, AppJsonContext.Default.Slv3State));

        // POST /devices/lianli-wireless/bind - request binding a discovered fan
        // to our master, into the first free slot. The connection worker's tick
        // drives the RF state machine until the device list confirms.
        app.MapPost("/devices/lianli-wireless/bind", (Slv3MacRequest body, Slv3Hub hub) =>
        {
            if (!hub.Bind(body.Mac))
            {
                return Results.Json(ApiResponse.Fail("invalid mac or no free slot"), AppJsonContext.Default.ApiResponse);
            }
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        // POST /devices/lianli-wireless/unbind - request releasing a fan from our master.
        app.MapPost("/devices/lianli-wireless/unbind", (Slv3MacRequest body, Slv3Hub hub) =>
        {
            if (!hub.Unbind(body.Mac))
            {
                return Results.Json(ApiResponse.Fail("invalid mac"), AppJsonContext.Default.ApiResponse);
            }
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        // POST /devices/lianli-wireless/identify - one-shot RF_Select flash.
        app.MapPost("/devices/lianli-wireless/identify", (Slv3MacRequest body, Slv3Hub hub) =>
        {
            if (!hub.Identify(body.Mac))
            {
                return Results.Json(ApiResponse.Fail("fan not found"), AppJsonContext.Default.ApiResponse);
            }
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        // POST /devices/lianli-wireless/reset-chain - soft-reboot a chain
        // controller stuck reporting header-only records (0 fans, no RPM).
        app.MapPost("/devices/lianli-wireless/reset-chain", (Slv3MacRequest body, Slv3Hub hub) =>
        {
            if (!hub.ResetChain(body.Mac))
            {
                return Results.Json(ApiResponse.Fail("fan not found"), AppJsonContext.Default.ApiResponse);
            }
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });
    }
}
