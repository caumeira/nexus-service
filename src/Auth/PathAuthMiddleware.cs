using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Routes;

namespace Nexus.Service.Auth;

// Path-based auth gate. Runs after UseRouting so we can read
// LocalhostOnlyAccess metadata off the matched endpoint, but in front of
// the route handler so a 401/403 short-circuits any state mutation.
//
// Auth order, top to bottom:
//   1. OPTIONS preflight — always passes.
//   2. Public paths (ping/pair/ready/hardware profile + a handful of phone-
//      pairing endpoints whose own handlers enforce per-request validation).
//   3. Static asset extensions (.js/.css/etc.) so the SPA bundle loads
//      without a token before the user has paired.
//   4. SPA shell fallback for unmatched top-level GET navigations.
//   5. Localhost-only routes (LocalhostOnlyAccess metadata) — 404 from LAN.
//   6. Bearer / query token — desktop session.
//   7. Phone session cookie / token — paired phone session, gated by the
//      Pair Remote killswitch.
internal static class PathAuthMiddleware
{
    private static readonly HashSet<string> AlwaysPublicPaths =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "/ping", "/pair", "/ready", "/hardware/profile",
            // Short-lived phone-panel pairing claims are validated at the handler level.
            "/panel/phone/claim",
        };

    // Per-method public paths. The phone QR opens the SPA shell without an
    // Authorization header; manual-code submit/confirm are validated by the
    // handler (rate limit, single-use, TTL, SAS binding).
    private static readonly HashSet<string> PublicGetPaths =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Pair Remote killswitch state. Public so paired phones can poll
            // for re-enable while their session is locked out; the body is
            // a single boolean and learning "host has disabled remotes" is
            // exactly the info a locked-out client needs.
            "/panel/phone/remote-control",
            // Cloud-relay opt-in state. Public read for the same reason as the
            // killswitch: a single boolean the panel / relay client may read
            // without a token. Write is desktop-token only.
            "/panel/phone/relay",
            "/panel/phone",
            // Wi-Fi broadcast preference. Public read so the iOS app can
            // tell the user "this PC isn't broadcasting" without already
            // being paired. Write is desktop-token only.
            "/panel/phone/pair-broadcast",
        };

    private static readonly HashSet<string> PublicPostPaths =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "/panel/phone/pair-code/submit",
            "/panel/phone/pair-code/confirm",
            // iOS Wi-Fi discovery → pair initiate. SAS-comparison handshake
            // with no 6-digit code; handler enforces the same rate limit as
            // /pair-code/submit.
            "/panel/phone/pair-wifi/initiate",
        };

    private static readonly string[] StaticAssetExtensions =
        { ".js", ".css", ".svg", ".png", ".ico", ".webmanifest", ".html", ".json", ".woff2" };

    private static bool IsPublicEndpoint(HttpContext ctx, string path)
    {
        if (AlwaysPublicPaths.Contains(path)) return true;
        if (ctx.Request.Method == "GET" && PublicGetPaths.Contains(path)) return true;
        if (ctx.Request.Method == "POST" && PublicPostPaths.Contains(path)) return true;
        // Module-worker code sessions carry a per-spawn 24-byte token in the
        // URL that the route handler validates. Bypassing here lets the
        // browser's ESM loader fetch sibling files inside a worker.
        if (ctx.Request.Method == "GET"
            && path.StartsWith("/widgets-api/code/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return false;
    }

    private static bool IsStaticAsset(string path)
    {
        foreach (var ext in StaticAssetExtensions)
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static IApplicationBuilder UseNexusPathAuth(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            if (string.Equals(ctx.Request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                await next(ctx);
                return;
            }

            var path = ctx.Request.Path.Value ?? string.Empty;

            if (IsPublicEndpoint(ctx, path) || IsStaticAsset(path))
            {
                await next(ctx);
                return;
            }

            // SPA shell fallback — unmatched top-level browser navigation.
            // GET-only is important: Sec-Fetch-Site: none also fires on POSTs
            // from the address bar (curl with no Origin), but a POST to a
            // state-changing endpoint must never ride the auth-bypass lane.
            if (AuthRequestPolicy.IsSpaShellFallbackAllowed(ctx))
            {
                await next(ctx);
                return;
            }

            // Localhost-only routes (service control: stop, startup-mode) get
            // 404 from any non-loopback caller before token validation, so the
            // route's existence is never leaked to LAN scanners.
            if (ctx.GetEndpoint()?.Metadata.GetMetadata<LocalhostOnlyAccess>() is not null)
            {
                var remote = ctx.Connection.RemoteIpAddress;
                if (remote is null || !IPAddress.IsLoopback(remote))
                {
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
            }

            var tokens = ctx.RequestServices.GetRequiredService<TokenService>();
            var panelPairing = ctx.RequestServices.GetRequiredService<Nexus.Service.Panel.PanelPhonePairingService>();
            var requestToken = AuthRequestPolicy.ExtractBearerOrQueryToken(ctx);
            if (tokens.Validate(requestToken))
            {
                await next(ctx);
                return;
            }

            var cookieToken = ctx.Request.Cookies[Nexus.Service.Panel.PanelPhonePairingService.SessionCookieName];
            var hasPanelSession =
                panelPairing.TryValidateSessionToken(requestToken, ctx, out var sessionId)
                || (!string.Equals(requestToken, cookieToken, StringComparison.Ordinal)
                    && panelPairing.TryValidateSessionToken(cookieToken, ctx, out sessionId));

            if (!hasPanelSession)
            {
                await AuthErrorResponse.WriteAsync(ctx, 401, "Unauthorized", "This panel is not paired with the Nexus service.");
                return;
            }

            // Pair Remote killswitch. When OFF, phone-session-authed requests
            // are rejected even though the session is otherwise valid. The
            // matching WS sockets have already been closed by
            // SetRemoteControlEnabledAsync; this guards new HTTP / WS upgrades.
            if (!panelPairing.GetRemoteControlEnabled())
            {
                await AuthErrorResponse.WriteAsync(ctx, 403, "RemoteDisabled", "Remote control is currently disabled.");
                return;
            }
            if (AuthRequestPolicy.RejectsInsecureCsrf(ctx))
            {
                await AuthErrorResponse.WriteAsync(ctx, 403, "CSRF", "Cross-site request blocked.");
                return;
            }
            if (!AuthRequestPolicy.IsPanelSessionAllowed(ctx))
            {
                await AuthErrorResponse.WriteAsync(ctx, 403, "Forbidden", "This action requires the desktop app.");
                return;
            }

            // Tag the request so the /ws upgrade can register the resulting
            // socket with MultiplexHub under this phone-session id - that's
            // how KickPhoneSessionsAsync / KickAllPhoneAsync find the right
            // sockets to close.
            if (!string.IsNullOrEmpty(sessionId))
                ctx.Items["PhoneSessionId"] = sessionId;
            await next(ctx);
        });
}
