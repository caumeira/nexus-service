using Microsoft.AspNetCore.Http;
using Nexus.Service.Models;
using Nexus.Service.Peripherals.Hyte.QSeriesCooler;

namespace Nexus.Service.Routes;

/// <summary>
/// HYTE Q-series (Q60 / Q80) cooler-controller device endpoints — the firmware
/// options surfaced on the Q60 device page's settings tab. The singleton
/// <see cref="QSeriesCoolerHub"/> holds live state polled from Port-0; pump
/// speed itself is driven through the cooling fan-channel path
/// (<c>POST /cooling/fan/qseries:…:pump/speed</c>), so these routes cover the
/// hub-wide firmware settings: control mode and turbo.
/// </summary>
public static partial class DevicesRoutes
{
    private static void MapQSeriesCoolerEndpoints(WebApplication app)
    {
        app.MapGet("/devices/qseries", (QSeriesCoolerHub hub) =>
            Results.Ok(new QSeriesCoolerStateResponse
            {
                Connected = hub.IsConnected,
                DeviceId = hub.DeviceId,
                ProductName = hub.ProductName,
                Variant = hub.Variant,
                FirmwareVersion = hub.State.FirmwareVersion,
                PumpRpm = hub.State.PumpRpm,
                Pump2Rpm = hub.State.Pump2Rpm,
                HasPump2 = hub.State.HasPump2,
                ControlMode = hub.State.ControlMode,
                TurboOn = hub.State.TurboOn,
            }));

        // Switch the hub control mode: Software (host drives), Motherboard
        // (mobo PWM), Firmware (onboard temperature curve).
        app.MapPut("/devices/qseries/control-mode", (QSeriesControlModeRequest body, QSeriesCoolerHub hub) =>
        {
            if (!hub.IsConnected)
                return Results.Conflict(new { error = "Q-series cooler not connected" });
            if (body.Mode < QSeriesCoolerProtocol.ControlModeSoftware
                || body.Mode > QSeriesCoolerProtocol.ControlModeMix)
            {
                return Results.BadRequest(new { error = "mode must be 1 (Software), 2 (Motherboard), 3 (Firmware), or 4 (Mix)" });
            }
            if (!hub.SetControlMode((byte)body.Mode))
                return Results.Problem("Failed to set Q-series control mode.");
            return Results.Ok(ApiResponse.Ok());
        });

        // Turbo unlocks the pump's full speed range (persisted to the MCU).
        app.MapPut("/devices/qseries/turbo", (QSeriesTurboRequest body, QSeriesCoolerHub hub) =>
        {
            if (!hub.IsConnected)
                return Results.Conflict(new { error = "Q-series cooler not connected" });
            if (!hub.SetTurbo(body.On))
                return Results.Problem("Failed to set Q-series turbo.");
            return Results.Ok(ApiResponse.Ok());
        });
    }
}

/// <summary>Shape returned by GET /devices/qseries. AOT-registered in <see cref="Serialization.AppJsonContext"/>.</summary>
public sealed class QSeriesCoolerStateResponse
{
    public bool Connected { get; set; }
    public string DeviceId { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string Variant { get; set; } = "";
    public string FirmwareVersion { get; set; } = "";
    public int PumpRpm { get; set; }
    public int Pump2Rpm { get; set; }
    public bool HasPump2 { get; set; }
    /// <summary>1=Software, 2=Motherboard, 3=Firmware, 4=Mix.</summary>
    public int ControlMode { get; set; }
    public bool TurboOn { get; set; }
}

/// <summary>Body for PUT /devices/qseries/control-mode.</summary>
public sealed class QSeriesControlModeRequest
{
    public int Mode { get; set; }
}

/// <summary>Body for PUT /devices/qseries/turbo.</summary>
public sealed class QSeriesTurboRequest
{
    public bool On { get; set; }
}
