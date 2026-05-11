using Qos.Service.Auth;
using Qos.Service.Models;
using Qos.Service.Models.Panel;
using Qos.Service.Panel;
using Qos.Service.Persistence;
using Qos.Service.Serialization;
using Qos.Service.Sockets;

namespace Qos.Service.Routes;

public static class PanelRoutes
{
    public static void MapPanelEndpoints(this WebApplication app)
    {
        app.MapGet("/panel/status", (PanelKioskLauncher launcher, MultiplexHub hub) =>
        {
            var phoneSubscribers = hub.TopicSubscriberCount(PanelPhonePairingService.PresenceTopic);
            return new PanelStatusResponse
            {
                Msg = launcher.IsRunning ? "running" : "stopped",
                KioskRunning = launcher.IsRunning,
                PhoneConnected = phoneSubscribers > 0,
                PhoneSubscribers = phoneSubscribers,
            };
        }).AllowPanel();

        app.MapGet("/panel/phone/pair-qr", (PanelPhonePairingService pairing) =>
            pairing.CreatePairQr());

        app.MapPost("/panel/phone/claim", (HttpContext ctx, PanelPhoneClaimBody body, PanelPhonePairingService pairing) =>
        {
            var result = pairing.Claim(body.PairToken, ctx);
            if (result.Paired && !string.IsNullOrWhiteSpace(result.Token))
            {
                ctx.Response.Cookies.Append(
                    PanelPhonePairingService.SessionCookieName,
                    result.Token,
                    new CookieOptions
                    {
                        HttpOnly = true,
                        Secure = ctx.Request.IsHttps,
                        SameSite = SameSiteMode.Lax,
                        Path = "/",
                        MaxAge = PanelPhonePairingService.SessionIdle,
                    });
            }

            return result.Paired
                ? Results.Ok(result)
                : Results.BadRequest(result);
        });

        app.MapGet("/panel/phone/service-info", (PanelPhonePairingService pairing) =>
            Results.Json(
                pairing.GetServiceInfo(),
                AppJsonContext.Default.PanelPhoneServiceInfoResponse))
            .AllowPanel();

        // User-overridable host PC display name. Reads pass through the
        // pairing service's settings-backed resolver, so a write here is
        // visible on the next /ping or /panel/phone/service-info call.
        // .AllowPanel() lets the panel settings sheet (kiosk + paired phones)
        // write directly; the field is cosmetic so any panel-authed surface
        // editing it is not a security concern.
        app.MapPost("/panel/host-name", (PanelHostNameBody? body, PanelPhonePairingService pairing) =>
        {
            var resolved = pairing.SetHostDisplayName(body?.Name);
            return Results.Json(
                new PanelHostNameResponse { MachineName = resolved },
                AppJsonContext.Default.PanelHostNameResponse);
        }).AllowPanel();

        app.MapGet("/panel/phone/sessions", (HttpContext ctx, PanelPhonePairingService pairing, MultiplexHub hub, TokenService tokens) =>
        {
            if (!HasServiceToken(ctx, tokens))
                return Results.Unauthorized();

            var phoneSubscribers = hub.TopicSubscriberCount(PanelPhonePairingService.PresenceTopic);
            return Results.Json(
                pairing.GetSessions(phoneSubscribers),
                AppJsonContext.Default.PanelPhoneSessionsResponse);
        });

        app.MapDelete("/panel/phone/sessions", (HttpContext ctx, PanelPhonePairingService pairing, TokenService tokens) =>
        {
            if (!HasServiceToken(ctx, tokens))
                return Results.Unauthorized();

            var removed = pairing.RevokeAllSessions();
            return Results.Ok(ApiResponse.Ok($"revoked {removed} sessions"));
        });

        app.MapPost("/panel/phone/sessions/{id}/name", (string id, PanelPhoneSessionNameBody body, HttpContext ctx, PanelPhonePairingService pairing, TokenService tokens) =>
        {
            if (!HasServiceToken(ctx, tokens))
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(body.Name))
                return Results.BadRequest(ApiResponse.Fail("name is required"));

            return pairing.RenameSession(id, body.Name)
                ? Results.Ok(ApiResponse.Ok("renamed"))
                : Results.NotFound(ApiResponse.Fail("session not found"));
        });

        app.MapDelete("/panel/phone/sessions/{id}", (string id, HttpContext ctx, PanelPhonePairingService pairing, TokenService tokens) =>
        {
            if (!HasServiceToken(ctx, tokens))
                return Results.Unauthorized();

            return pairing.RevokeSession(id)
                ? Results.Ok(ApiResponse.Ok("revoked"))
                : Results.NotFound(ApiResponse.Fail("session not found"));
        });

        app.MapGet("/panel/devices", (PanelDeviceRegistry registry) =>
        {
            return Results.Json(
                new PanelDeviceListResponse { Devices = registry.List().ToList() },
                AppJsonContext.Default.PanelDeviceListResponse);
        }).AllowPanel();

        app.MapPost("/panel/devices", (PanelDeviceCreateBody? body, PanelDeviceRegistry registry, MultiplexHub hub) =>
        {
            var record = registry.Allocate(body?.DisplayName, body?.Capabilities);
            BroadcastDeviceChanged(hub, record.Id);
            return Results.Json(record, AppJsonContext.Default.PanelDeviceRecord);
        }).AllowPanel();

        app.MapGet("/panel/devices/{id}", (string id, PanelDeviceRegistry registry) =>
        {
            var record = registry.Get(id);
            if (record is null)
                return Results.NotFound(ApiResponse.Fail("device not found"));
            // Persisted layout if present, else the generic starter. The
            // starter isn't written back; it only persists once the client
            // posts an edit.
            if (record.Layout is null)
                record.Layout = PanelLayoutDefaults.Default();
            registry.Touch(id);
            return Results.Json(record, AppJsonContext.Default.PanelDeviceRecord);
        }).AllowPanel();

        app.MapPost("/panel/devices/{id}", (string id, PanelDevicePatch body, PanelDeviceRegistry registry, MultiplexHub hub) =>
        {
            var updated = registry.Patch(id, body);
            if (updated is null)
                return Results.NotFound(ApiResponse.Fail("device not found"));
            // PanelDevices is hardware-scoped (top-level on QosSettings),
            // not part of any profile snapshot. Registry mutations already go
            // through _store.Update -> OnChanged -> ProfileManager.MarkDirty
            // for the active-profile flush of OTHER fields; we don't need the
            // explicit dirty pulse here, and keeping it implied PanelDevices
            // was profile-scoped, which is the bug this change fixes.
            BroadcastDeviceChanged(hub, id);
            return Results.Json(updated, AppJsonContext.Default.PanelDeviceRecord);
        }).AllowPanel();

        app.MapDelete("/panel/devices/{id}", (string id, HttpContext ctx, PanelDeviceRegistry registry, MultiplexHub hub, TokenService tokens) =>
        {
            if (!HasServiceToken(ctx, tokens))
                return Results.Unauthorized();
            if (!registry.Remove(id))
                return Results.NotFound(ApiResponse.Fail("device not found"));
            BroadcastDeviceChanged(hub, id);
            return Results.Ok(ApiResponse.Ok("removed"));
        });
    }

    private static void BroadcastDeviceChanged(MultiplexHub hub, string deviceId)
        => PanelTopics.BroadcastPanelDevice(hub, deviceId);

    private static bool HasServiceToken(HttpContext ctx, TokenService tokens)
    {
        var authHeader = ctx.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return tokens.Validate(authHeader.Substring("Bearer ".Length).Trim());

        var queryToken = ctx.Request.Query["token"].ToString();
        return tokens.Validate(queryToken);
    }
}
