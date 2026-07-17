using Nexus.Service.Auth;
using Nexus.Service.Models;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Platform.Displays;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Routes;

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
            var result = pairing.Claim(body.PairToken, body.DeviceId, body.DeviceName, ctx, body.SupportsSasApproval);
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

            if (result.Paired || result.NeedsApproval)
            {
                return Results.Ok(result);
            }
            return Results.BadRequest(result);
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

        app.MapDelete("/panel/phone/sessions", async (HttpContext ctx, PanelPhonePairingService pairing, TokenService tokens) =>
        {
            if (!HasServiceToken(ctx, tokens))
                return Results.Unauthorized();

            var removed = await pairing.RevokeAllSessionsAsync();
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

        app.MapDelete("/panel/phone/sessions/{id}", async (string id, HttpContext ctx, PanelPhonePairingService pairing, TokenService tokens) =>
        {
            if (!HasServiceToken(ctx, tokens))
                return Results.Unauthorized();

            return await pairing.RevokeSessionAsync(id)
                ? Results.Ok(ApiResponse.Ok("revoked"))
                : Results.NotFound(ApiResponse.Fail("session not found"));
        });

        // Pair Remote killswitch. GET is public (the auth middleware whitelists
        // the path) so a locked-out phone can poll for re-enable instead of
        // freezing on stale data. POST is desktop-token only: paired remotes
        // can never re-enable themselves after being kicked, and the panel
        // kiosk cannot accidentally take its own remotes offline. The local
        // desktop UI already has the /pair token.
        app.MapGet("/panel/phone/remote-control", (PanelPhonePairingService pairing) =>
        {
            return Results.Json(
                new RemoteControlStateResponse { Enabled = pairing.GetRemoteControlEnabled() },
                AppJsonContext.Default.RemoteControlStateResponse);
        });

        app.MapPost("/panel/phone/remote-control", async (RemoteControlToggleRequest body, HttpContext ctx, PanelPhonePairingService pairing, TokenService tokens) =>
        {
            if (!HasServiceToken(ctx, tokens))
                return Results.Unauthorized();

            await pairing.SetRemoteControlEnabledAsync(body.Enabled);
            return Results.Json(
                new RemoteControlStateResponse { Enabled = pairing.GetRemoteControlEnabled() },
                AppJsonContext.Default.RemoteControlStateResponse);
        });

        // Cloud-relay transport opt-in. GET is public (same rationale as the
        // killswitch GET: a relay client / panel can read the state without a
        // token). POST is desktop-token only - only a user at the PC may turn
        // the relay on/off, matching the remote-control toggle. Persisting the
        // change fires IConfigStore.OnChanged, which RelayConnectionService
        // listens on to open / tear down its host sockets (no poll loop).
        app.MapGet("/panel/phone/relay", (PanelPhonePairingService pairing) =>
        {
            return Results.Json(
                new RelayStateResponse { Enabled = pairing.GetRelayEnabled() },
                AppJsonContext.Default.RelayStateResponse);
        });

        app.MapPost("/panel/phone/relay", (RelayToggleRequest body, HttpContext ctx, PanelPhonePairingService pairing, TokenService tokens) =>
        {
            if (!HasServiceToken(ctx, tokens))
                return Results.Unauthorized();

            pairing.SetRelayEnabled(body.Enabled);
            return Results.Json(
                new RelayStateResponse { Enabled = pairing.GetRelayEnabled() },
                AppJsonContext.Default.RelayStateResponse);
        });

        // Wi-Fi (mDNS) discoverability preference. Public read; desktop-token write.
        app.MapGet("/panel/phone/pair-broadcast", (PanelPhonePairingService pairing) =>
        {
            var (mode, until) = pairing.GetPairBroadcast();
            return Results.Json(
                new PairBroadcastStateResponse { Mode = mode, UntilUnixSeconds = until },
                AppJsonContext.Default.PairBroadcastStateResponse);
        });

        app.MapPost("/panel/phone/pair-broadcast", (PairBroadcastSetRequest body, HttpContext ctx, PanelPhonePairingService pairing, TokenService tokens) =>
        {
            if (!HasServiceToken(ctx, tokens))
                return Results.Unauthorized();

            pairing.SetPairBroadcast(body.Mode ?? "always", body.UntilUnixSeconds);
            var (mode, until) = pairing.GetPairBroadcast();
            return Results.Json(
                new PairBroadcastStateResponse { Mode = mode, UntilUnixSeconds = until },
                AppJsonContext.Default.PairBroadcastStateResponse);
        });

        // iOS Wi-Fi discovery → tap → initiate. Same SAS-comparison handshake as
        // /pair-code/submit but with no 6-digit code - phone discovered us over
        // Bonjour, user's Allow click on the desktop is the OOB authentication.
        // Public (auth-bypassed in PathAuthMiddleware) and rate-limited inside
        // the service (per-IP lockout shared with the code flow).
        app.MapPost("/panel/phone/pair-wifi/initiate", (HttpContext ctx, PairWifiInitiateRequest body, PanelPhonePairingService pairing) =>
        {
            var result = pairing.InitiatePairWifi(body?.DeviceName ?? "", ctx);
            return result.Accepted
                ? Results.Json(result, AppJsonContext.Default.PanelPhonePairCodeSubmitResponse)
                : Results.Json(result, AppJsonContext.Default.PanelPhonePairCodeSubmitResponse, statusCode: result.Error == "rate-limited" ? 429 : 400);
        });

        // Manual pair-code flow. /start + /host-decision require the desktop
        // token (only a user at the PC can mint / approve a code). /submit
        // and /confirm are public so the phone (no session yet) can drive its
        // half; the auth bypass is added to the middleware list in Program.cs.
        app.MapPost("/panel/phone/pair-code/start", (HttpContext ctx, PanelPhonePairingService pairing, TokenService tokens) =>
        {
            if (!HasServiceToken(ctx, tokens))
                return Results.Unauthorized();
            return Results.Json(pairing.StartPairCode(), AppJsonContext.Default.PanelPhonePairCodeStartResponse);
        });

        app.MapPost("/panel/phone/pair-code/submit", (HttpContext ctx, PanelPhonePairCodeSubmitBody body, PanelPhonePairingService pairing) =>
        {
            var result = pairing.SubmitPairCode(body?.Code ?? "", ctx);
            return result.Accepted
                ? Results.Json(result, AppJsonContext.Default.PanelPhonePairCodeSubmitResponse)
                : Results.Json(result, AppJsonContext.Default.PanelPhonePairCodeSubmitResponse, statusCode: result.Error == "rate-limited" ? 429 : 400);
        });

        app.MapPost("/panel/phone/pair-code/confirm", (HttpContext ctx, PanelPhonePairCodeConfirmBody body, PanelPhonePairingService pairing) =>
        {
            var result = pairing.ConfirmPairCode(body?.RequestId ?? "", body?.Approved ?? false, ctx);
            if (result.Status == "approved" && !string.IsNullOrEmpty(result.Token))
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
            return Results.Json(result, AppJsonContext.Default.PanelPhonePairCodeConfirmResponse);
        });

        app.MapPost("/panel/phone/pair-code/host-decision", (HttpContext ctx, PanelPhonePairCodeHostDecisionBody body, PanelPhonePairingService pairing, TokenService tokens) =>
        {
            if (!HasServiceToken(ctx, tokens))
                return Results.Unauthorized();
            var result = pairing.HostDecisionPairCode(body?.RequestId ?? "", body?.Approved ?? false);
            return Results.Json(result, AppJsonContext.Default.PanelPhonePairCodeHostDecisionResponse);
        });

        // The console user's current wallpaper, cropped to the requesting
        // panel monitor's resolution when the shell has that crop cached.
        // Panels render it as the background-off ("desktop") background: the
        // wallpaper look without icons, taskbar, or windows. no-cache: the
        // client busts via a revision query param on wallpaper-change frames.
        app.MapGet("/panel/desktop-wallpaper", (HttpContext ctx, int? width, int? height) =>
        {
            var path = DesktopWallpaperProvider.TryResolve(width ?? 0, height ?? 0);
            if (path is null)
                return Results.NotFound(ApiResponse.Fail("wallpaper unavailable"));
            ctx.Response.Headers.CacheControl = "no-cache";
            return Results.File(path, "image/jpeg");
        }).AllowPanel();

        app.MapGet("/panel/devices", (PanelDeviceRegistry registry, Platform.Displays.DisplayTopologyService topology) =>
        {
            var devices = registry.List().ToList();
            // displayAttached is response-only state for display-bound records
            // (promoted monitors): false hides the row while the monitor is
            // unplugged, null = topology unknown (no helper), so the UI keeps
            // showing the panel rather than flickering it away.
            if (devices.Any(d => !string.IsNullOrEmpty(d.DisplayId)))
            {
                var attached = topology.GetAttachedIds();
                foreach (var device in devices)
                {
                    if (!string.IsNullOrEmpty(device.DisplayId))
                        device.DisplayAttached = attached?.Contains(device.DisplayId);
                }
            }
            return Results.Json(
                new PanelDeviceListResponse { Devices = devices },
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
            // Persisted layout if present, else the surface-specific
            // starter. The starter isn't written back; it only persists once
            // the client posts an edit. Falls back to the y70 seed when the
            // device record has no capabilities yet (pre-handshake GETs).
            if (record.Layout is null)
                record.Layout = PanelLayoutDefaults.ForSurface(record.Capabilities?.Surface ?? "y70");
            registry.Touch(id);
            return Results.Json(record, AppJsonContext.Default.PanelDeviceRecord);
        }).AllowPanel();

        app.MapPost("/panel/devices/{id}", (string id, PanelDevicePatch body, PanelDeviceRegistry registry, MultiplexHub hub) =>
        {
            var updated = registry.Patch(id, body);
            if (updated is null)
                return Results.NotFound(ApiResponse.Fail("device not found"));
            // PanelDevices is hardware-scoped (top-level on NexusSettings),
            // not part of any profile snapshot. Registry mutations already go
            // through _store.Update -> OnChanged -> ProfileManager.MarkDirty
            // for the active-profile flush of OTHER fields; we don't need the
            // explicit dirty pulse here, and keeping it implied PanelDevices
            // was profile-scoped, which is the bug this change fixes.
            BroadcastDeviceChanged(hub, id);
            return Results.Json(updated, AppJsonContext.Default.PanelDeviceRecord);
        }).AllowPanel();

        // Personalization reset: the record's layout/theme/background/widget
        // fields return to their just-allocated state (defaults reseed on the
        // next read) and every uploaded media asset is deleted. Identity,
        // display binding, capabilities, and hardware-scoped settings
        // survive. Dashboard-only (service token), same trust level as
        // deleting the device.
        app.MapPost("/panel/devices/{id}/reset", (string id, HttpContext ctx, PanelDeviceRegistry registry, PanelBgLibrary bgLibrary, MultiplexHub hub, TokenService tokens) =>
        {
            if (!HasServiceToken(ctx, tokens))
                return Results.Unauthorized();
            var reset = registry.ResetToDefaults(id);
            if (reset is null)
                return Results.NotFound(ApiResponse.Fail("device not found"));
            if (!bgLibrary.DeleteDeviceMedia(id))
                ServiceLog.Warn($"[panel] reset: media dir for '{id}' could not be fully removed");
            BroadcastDeviceChanged(hub, id);
            return Results.Json(reset, AppJsonContext.Default.PanelDeviceRecord);
        });

        // Hardware-settings reset: everything the device's Settings tab
        // drives returns to defaults and is APPLIED to the hardware - Y70
        // brightness/orientation/screen + panel auto-launch, Q-series
        // brightness/orientation/screen/sleep-with-host, Xeneon Edge DDC
        // picture values - plus the record's monitor behavior
        // (ReserveMonitor/AutoOrient). Personalization is untouched.
        app.MapPost("/panel/devices/{id}/reset-hardware", async (string id, HttpContext ctx, PanelDeviceRegistry registry, IConfigStore store, IServiceProvider sp, MultiplexHub hub, TokenService tokens, CancellationToken ct) =>
        {
            if (!HasServiceToken(ctx, tokens))
                return Results.Unauthorized();
            var record = registry.ResetHardwareSettings(id);
            if (record is null)
                return Results.NotFound(ApiResponse.Fail("device not found"));

            var surface = record.Capabilities?.Surface;
            var family = record.Capabilities?.Family;
            if (string.Equals(surface, PanelSurfaces.Y70, StringComparison.Ordinal))
            {
                var defaults = new Y70Settings();
                // AutoLaunch is workstation-level and gates every panel
                // kiosk, but its toggle lives on the Y70 Settings tab and
                // the Y70 is single-instance per host, so its device reset
                // owns it. Broadcast so open dashboards see the flip.
                store.Update(s => s.Panel.AutoLaunch = new PanelSettings().AutoLaunch);
                PanelTopics.BroadcastPrefs(hub);
                var y70 = sp.GetRequiredService<Peripherals.Y70.IY70Provider>();
                y70.SetOrientation(defaults.Orientation);
                y70.SetForceOrientation(defaults.ForceOrientation);
                y70.ApplyEffectiveOrientation();
                y70.SetBrightness(defaults.Brightness);
                y70.SetToggle(defaults.ScreenOff);
            }
            else if (string.Equals(surface, PanelSurfaces.Q60, StringComparison.Ordinal))
            {
                // Property initializers carry the install defaults; the
                // watcher diffs against confirmed state and applies.
                store.Update(s => s.QSeries = new QSeriesSettings());
                sp.GetService<Nexus.Service.QSeries.QSeriesPortWatcher>()?.AnnounceDisplayChange();
            }
            else if (string.Equals(family, KnownPanelDisplays.XeneonEdgeFamily, StringComparison.Ordinal)
                && record.DisplayId is { Length: > 0 } displayId)
            {
                var xeneon = sp.GetService<Peripherals.Corsair.XeneonEdge.XeneonEdgeOrientationWorker>();
                if (xeneon is null || !await xeneon.RestoreDefaultsAsync(ct))
                {
                    // The record-side reset above already flushed; broadcast
                    // it so clients don't render a silently reverted record,
                    // then surface the DDC failure (the POST is idempotent,
                    // a retry re-runs everything).
                    BroadcastDeviceChanged(hub, id);
                    return Results.UnprocessableEntity(ApiResponse.Fail("hardware restore failed"));
                }
                registry.UpdateXeneonEdgeSettings(displayId, new XeneonEdgeSettingsDto
                {
                    Brightness = Peripherals.Corsair.XeneonEdge.XeneonEdgeDefaults.Brightness,
                    Backlight = Peripherals.Corsair.XeneonEdge.XeneonEdgeDefaults.Backlight,
                    Contrast = Peripherals.Corsair.XeneonEdge.XeneonEdgeDefaults.Contrast,
                    Red = Peripherals.Corsair.XeneonEdge.XeneonEdgeDefaults.Red,
                    Green = Peripherals.Corsair.XeneonEdge.XeneonEdgeDefaults.Green,
                    Blue = Peripherals.Corsair.XeneonEdge.XeneonEdgeDefaults.Blue,
                });
            }
            BroadcastDeviceChanged(hub, id);
            var updated = registry.Get(id);
            return updated is null
                ? Results.NotFound(ApiResponse.Fail("device not found"))
                : Results.Json(updated, AppJsonContext.Default.PanelDeviceRecord);
        });

        app.MapDelete("/panel/devices/{id}", (string id, HttpContext ctx, PanelDeviceRegistry registry, PanelBgLibrary bgLibrary, MultiplexHub hub, TokenService tokens) =>
        {
            if (!HasServiceToken(ctx, tokens))
                return Results.Unauthorized();
            if (!registry.Remove(id))
                return Results.NotFound(ApiResponse.Fail("device not found"));
            // Single-instance surfaces mint a fresh id on the next allocate,
            // so a removed device's media dir would be orphaned forever.
            if (!bgLibrary.DeleteDeviceMedia(id))
                ServiceLog.Warn($"[panel] remove: media dir for '{id}' could not be fully removed");
            BroadcastDeviceChanged(hub, id);
            return Results.Ok(ApiResponse.Ok("removed"));
        });
    }

    private static void BroadcastDeviceChanged(MultiplexHub hub, string deviceId)
        => PanelTopics.BroadcastPanelDevice(hub, deviceId);

    private static bool HasServiceToken(HttpContext ctx, TokenService tokens)
        => Auth.ServiceTokenRequests.HasServiceToken(ctx, tokens);
}
