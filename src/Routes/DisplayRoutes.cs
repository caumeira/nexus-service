using Nexus.Service.Auth;
using Nexus.Service.Models;
using Nexus.Service.Models.Displays;
using Nexus.Service.Models.Panel;
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
        app.MapGet("/y70/rotation", (IY70Provider y) => new Y70RotationParams
        {
            Orientation = y.GetOrientation(),
            ForceOrientation = y.GetForceOrientation(),
        }).AllowPanel();
        app.MapPost("/y70/rotation", (Y70RotationParams body, IY70Provider y) =>
        {
            var changed = false;
            if (body.Orientation is not null) { y.SetOrientation(body.Orientation); changed = true; }
            if (body.ForceOrientation is not null) { y.SetForceOrientation(body.ForceOrientation.Value); changed = true; }
            if (changed) y.ApplyEffectiveOrientation();
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
            // /panel/{id} directly and never runs the self-report path.
            // SyncPromotedPanelCapabilities re-derives the same shape on
            // later topology reads, so the record tracks rotation/rescale.
            var capabilities = DisplayTopologyService.BuildPromotedCapabilities(
                display.Manufacturer, display.Model, display.Name,
                display.Resolution.Width, display.Resolution.Height,
                display.ScaleFactor, display.IsTouch, display.Orientation);
            var (record, activated) = registry.AllocateForDisplay(id, body?.DisplayName ?? display.Name, capabilities);
            if (!activated)
                return Results.Conflict(ApiResponse.Fail("display is already a panel"));
            PanelTopics.BroadcastPanelDevice(hub, record.Id);
            PanelTopics.BroadcastDisplays(hub);
            return Results.Json(record, AppJsonContext.Default.PanelDeviceRecord);
        });

        // Turn the panel OFF: the record (layout/theme/settings) persists so
        // turning it back on restores the panel exactly; assignments stop
        // listing it and the overlay closes the kiosk. Full record deletion
        // stays available via DELETE /panel/devices/{id}.
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
            if (record is null || record.Capabilities?.Surface != PanelSurfaces.Monitor || record.Enabled == false)
                return Results.NotFound(ApiResponse.Fail("display is not an active panel"));
            registry.DisablePanelForDisplay(id);
            PanelTopics.BroadcastPanelDevice(hub, record.Id);
            PanelTopics.BroadcastDisplays(hub);
            return Results.Ok(ApiResponse.Ok("panel turned off"));
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
