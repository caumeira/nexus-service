using System;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Models;
using Nexus.Service.Peripherals.Hyte.MiniHub;

namespace Nexus.Service.Routes;

/// <summary>
/// HYTE MiniHub (iBUYPOWER rebrand) live-control endpoints. Mirrors the
/// shape of <see cref="DevicesRoutes.MapNp50Endpoints"/> for the bits that
/// the panel calls into. The MiniHub firmware exposes only two fan modes
/// (Software and Motherboard) and has no EEPROM default-mode surface, so
/// no firmware-defaults / firmware-animation routes here.
/// </summary>
public static partial class DevicesRoutes
{
    private static void MapMiniHubEndpoints(WebApplication app)
    {
        // Switch the active fan-control mode and PIN it. Pinning matters:
        // MiniHubCoolingProvider's per-write guard reads back this pin to
        // decide whether to issue PWM writes that would otherwise re-assert
        // Software. Without the pin, the cooling page's per-fan BIOS pick
        // would silently revert on the next curve tick.
        app.MapPut("/devices/minihub/cooling-mode", (MiniHubCoolingModeRequest body, MiniHubHub hub) =>
        {
            if (!hub.IsConnected)
                return Results.Conflict(new { error = "MiniHub not connected" });
            // 0 = Software (Nexus drives), 1 = Motherboard (PWM passthrough).
            // Reject anything else so a stray client doesn't park the hub in
            // an undocumented mode.
            if (body.Mode != MiniHubProtocol.FanModeSoftware
                && body.Mode != MiniHubProtocol.FanModeMotherboard)
            {
                return Results.BadRequest(new { error = "mode must be 0 (Software) or 1 (Motherboard)" });
            }
            hub.SetDesiredFanControlMode((byte)body.Mode);
            return Results.Ok(ApiResponse.Ok());
        });
    }
}

/// <summary>Body shape for PUT /devices/minihub/cooling-mode.</summary>
public sealed class MiniHubCoolingModeRequest
{
    /// <summary>0=Software, 1=Motherboard (see <see cref="MiniHubProtocol"/>).</summary>
    public int Mode { get; set; }
}
