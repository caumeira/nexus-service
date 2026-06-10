using System;
using Nexus.Service.Auth;
using Nexus.Service.Models;
using Nexus.Service.Models.Displays;
using Nexus.Service.Models.Panel;
using Nexus.Service.Models.Peripherals.QSeries;
using Nexus.Service.Models.Peripherals.Y70;
using Nexus.Service.Panel;
using Nexus.Service.Peripherals.QSeries;
using Nexus.Service.Peripherals.Y70;
using Nexus.Service.Platform.Displays;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Routes;

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

        // OS monitor topology (positions, modes, scale) merged with panel state.
        app.MapGet("/displays/topology", (DisplayTopologyService topology) => topology.GetTopology()).AllowPanel();

        // displayId -> panelDeviceId bindings; the overlay reconciles its
        // kiosk windows against this on every prefs poll / push.
        app.MapGet("/displays/assignments", (HttpContext ctx, PanelDeviceRegistry registry, TokenService tokens) =>
        {
            if (!ServiceTokenRequests.HasServiceToken(ctx, tokens))
                return Results.Unauthorized();
            var response = new DisplayAssignmentsResponse();
            foreach (var (displayId, panelDeviceId, reserveMonitor) in registry.ListAssignments())
            {
                response.Assignments.Add(new DisplayAssignmentDto
                {
                    DisplayId = displayId,
                    PanelDeviceId = panelDeviceId,
                    ReserveMonitor = reserveMonitor,
                });
            }
            return Results.Json(response, AppJsonContext.Default.DisplayAssignmentsResponse);
        });

        // Promote a monitor to a Nexus panel: allocates the panel device
        // record that becomes the kiosk's identity (/panel/{recordId}).
        app.MapPost("/displays/{id}/panel", (
            string id,
            PanelPromoteBody? body,
            HttpContext ctx,
            DisplayTopologyService topology,
            PanelDeviceRegistry registry,
            MultiplexHub hub,
            TokenService tokens) =>
        {
            if (!ServiceTokenRequests.HasServiceToken(ctx, tokens))
                return Results.Unauthorized();
            if (!DisplayTopologyService.HostingSupportedOnHost)
                return Results.UnprocessableEntity(ApiResponse.Fail("panel hosting is not supported on this system yet"));

            var display = topology.FindDisplay(id);
            if (display is null)
                return Results.NotFound(ApiResponse.Fail("display not found"));
            if (display.IsY70)
                return Results.Conflict(ApiResponse.Fail("the Y70 panel is managed automatically"));
            if (display.AssignedPanelDeviceId is not null)
                return Results.Conflict(ApiResponse.Fail("display is already a panel"));

            // Stamp viewport hints from the OS facts: the kiosk loads
            // /panel/{id} directly and never runs the self-report path, so
            // promote-time values are what the dashboard simulator sees.
            var scale = display.ScaleFactor ?? 1.0;
            var capabilities = new PanelDeviceCapabilities
            {
                Surface = PanelSurfaces.Monitor,
                // Touch widgets are placeable only when an integrated touch
                // digitizer targets this monitor (Windows pointer-device
                // association); plain monitors behave like the Q-series.
                Touch = display.IsTouch,
                Orientation = string.IsNullOrEmpty(display.Orientation) ? null : display.Orientation,
                CssWidth = (int)Math.Round(display.Resolution.Width / scale),
                CssHeight = (int)Math.Round(display.Resolution.Height / scale),
                Dpr = scale,
            };
            var (record, created) = registry.AllocateForDisplay(id, body?.DisplayName ?? display.Name, capabilities);
            if (!created)
                return Results.Conflict(ApiResponse.Fail("display is already a panel"));
            PanelTopics.BroadcastPanelDevice(hub, record.Id);
            PanelTopics.BroadcastDisplays(hub);
            return Results.Json(record, AppJsonContext.Default.PanelDeviceRecord);
        });

        // Demote: delete the record (and its layout) and release the monitor.
        app.MapDelete("/displays/{id}/panel", (
            string id,
            HttpContext ctx,
            PanelDeviceRegistry registry,
            MultiplexHub hub,
            TokenService tokens) =>
        {
            if (!ServiceTokenRequests.HasServiceToken(ctx, tokens))
                return Results.Unauthorized();
            var record = registry.FindByDisplayId(id);
            if (record is null || record.Capabilities?.Surface != PanelSurfaces.Monitor)
                return Results.NotFound(ApiResponse.Fail("display is not a panel"));
            registry.Remove(record.Id);
            PanelTopics.BroadcastPanelDevice(hub, record.Id);
            PanelTopics.BroadcastDisplays(hub);
            return Results.Ok(ApiResponse.Ok("removed"));
        });

        // Rotate any monitor by stable display id (promoted-panel settings).
        // Runs through the user-session helper like the Y70 rotation; the
        // resulting WM_DISPLAYCHANGE re-broadcasts the displays topic.
        app.MapPost("/displays/{id}/rotation", (
            string id,
            DisplayRotationBody body,
            HttpContext ctx,
            IDisplayOrientationProvider orientation,
            PanelDeviceRegistry registry,
            MultiplexHub hub,
            TokenService tokens) =>
        {
            if (!ServiceTokenRequests.HasServiceToken(ctx, tokens))
                return Results.Unauthorized();
            if (!DisplayOrientations.IsValid(body.Orientation))
                return Results.BadRequest(ApiResponse.Fail($"unknown orientation '{body.Orientation}'"));
            var (ok, error) = orientation.SetDisplayOrientation(id, body.Orientation);
            if (!ok)
                return Results.BadRequest(ApiResponse.Fail(string.IsNullOrEmpty(error) ? "rotation failed" : error));
            // Settings permanence: remember the applied orientation on the
            // bound record (when this display is a panel), same model as the
            // Y70's persisted orientation.
            var record = registry.FindByDisplayId(id);
            if (record is not null)
            {
                registry.UpdateDisplayOrientation(id, body.Orientation);
                PanelTopics.BroadcastPanelDevice(hub, record.Id);
            }
            return Results.Ok(ApiResponse.Ok("rotated"));
        });

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

    }
}
