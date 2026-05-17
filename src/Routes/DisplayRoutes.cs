using Qos.Service.Auth;
using Qos.Service.Models;
using Qos.Service.Models.Displays;
using Qos.Service.Models.Peripherals.QSeries;
using Qos.Service.Models.Peripherals.Y70;
using Qos.Service.Peripherals.QSeries;
using Qos.Service.Peripherals.Y70;
using Qos.Service.Platform.Displays;

namespace Qos.Service.Routes;

public static class DisplayRoutes
{
    public static void MapDisplayEndpoints(this WebApplication app)
    {
        // Y70
        app.MapGet("/y70/status", (IY70Provider y) => new Y70StatusResponse { IsConnected = y.IsConnected() }).AllowPanel();
        app.MapGet("/y70/rotation", (IY70Provider y) => new Y70RotationParams { Orientation = y.GetOrientation() }).AllowPanel();
        app.MapPost("/y70/rotation", (Y70RotationParams body, IY70Provider y) =>
        {
            y.SetOrientation(body.Orientation);
            return ApiResponse.Ok();
        }).AllowPanel();
        app.MapGet("/y70/brightness", (IY70Provider y) => new Y70BrightnessResponse { Brightness = y.GetBrightness() }).AllowPanel();
        app.MapPost("/y70/brightness", (Y70BrightnessParams body, IY70Provider y) =>
        {
            y.SetBrightness(body.Brightness);
            return new Y70BrightnessResponse { Brightness = body.Brightness };
        }).AllowPanel();
        app.MapGet("/y70/toggle", (IY70Provider y) => new Y70ToggleScreenResponse { Toggle = y.GetToggle() }).AllowPanel();
        app.MapPost("/y70/toggle", (Y70ToggleScreenParams body, IY70Provider y) =>
        {
            y.SetToggle(body.Toggle);
            return new Y70BrightnessResponse { Brightness = 20 };
        }).AllowPanel();
        app.MapGet("/y70/is-rotated", (IY70Provider y) => new Y70IsRotatedResponse { IsRotated = y.IsRotated() }).AllowPanel();

        // Q-series (Q60 + Q80)
        app.MapGet("/qseries/serial", (IQSeriesProvider q) => new GetSerialNumberResponse { Serial = q.GetSerial() });
        app.MapGet("/qseries/timev0", (IQSeriesProvider q) => new GetQSeriesTimeResponse { Time = q.GetFormattedTime() });

        // System monitors (external DDC/CI + internal panels)
        app.MapGet("/displays", (DisplayBrightnessController d) => d.ListDisplays()).AllowPanel();

        app.MapGet("/displays/{id}/brightness", (string id, DisplayBrightnessController d) =>
        {
            var v = d.GetBrightness(id);
            return v.HasValue
                ? Results.Ok(new DisplayBrightnessDto
                {
                    Id = id,
                    Brightness = v.Value,
                    RequestedBrightness = v.Value,
                    AppliedBrightness = v.Value,
                    Status = DisplayBrightnessWriteStatuses.Applied,
                })
                : Results.NotFound();
        }).AllowPanel();

        app.MapPost("/displays/{id}/brightness", async (
            string id,
            DisplayBrightnessParams body,
            DisplayBrightnessController d,
            CancellationToken ct) =>
        {
            var result = await d.SetBrightnessAsync(id, body.Brightness, ct);
            return result.Status == DisplayBrightnessWriteStatuses.Applied
                ? Results.Ok(result)
                : Results.BadRequest(result);
        }).AllowPanel();

        app.MapGet("/displays/{id}/vcp/{code:int}", (string id, int code, IDisplayBrightnessProvider d) =>
        {
            if (code is < 0 or > 255) return Results.BadRequest();
            var dto = d.GetVcp(id, (byte)code);
            return dto is not null ? Results.Ok(dto) : Results.NotFound();
        });

        app.MapPost("/displays/{id}/vcp/{code:int}", (string id, int code, DisplayVcpParams body, IDisplayBrightnessProvider d) =>
        {
            if (code is < 0 or > 255) return Results.BadRequest();
            return d.SetVcp(id, (byte)code, body.Value)
                ? Results.Ok(new DisplayVcpDto { Id = id, Code = (byte)code, Value = body.Value, MaxValue = 0 })
                : Results.BadRequest();
        });
    }
}
