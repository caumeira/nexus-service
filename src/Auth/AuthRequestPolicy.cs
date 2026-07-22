using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Nexus.Service.Auth;

public static class AuthRequestPolicy
{
    /// <summary>
    /// True when the request arrived on the loopback interface (127.0.0.1 / ::1).
    /// A null remote address (a synthetic in-process request with no socket) is
    /// treated as NOT loopback so the shell and desktop-token gates fail closed.
    /// </summary>
    public static bool IsLoopbackRemote(HttpContext ctx)
    {
        var addr = ctx.Connection.RemoteIpAddress;
        return addr is not null && IPAddress.IsLoopback(addr);
    }

    /// <summary>
    /// Whether the SPA shell (index.html) may be served to this caller. The
    /// desktop dashboard is loopback-only; the phone-panel (<c>/panel</c>) and
    /// pairing deep-link (<c>/r</c>) surfaces are the only SPA entry points a
    /// LAN or relay client may load. Any other navigation off the loopback
    /// interface is 404'd before the shell is served, so the dashboard UI is
    /// never reachable from another machine.
    /// </summary>
    public static bool IsShellReachable(HttpContext ctx)
    {
        if (IsLoopbackRemote(ctx))
            return true;
        var path = ctx.Request.Path;
        return path.StartsWithSegments("/panel", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/r", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the request targets the root dashboard shell document
    /// (<c>index.html</c>) in any slash-spelling. Kestrel normalizes dot-segments
    /// but NOT repeated slashes, and UseDefaultFiles/UseStaticFiles serve the root
    /// index.html for <c>/</c>, <c>//</c>, <c>///index.html</c>, etc. Collapse
    /// repeated slashes and strip a trailing slash before matching so no spelling
    /// serves the shell off the loopback interface.
    /// </summary>
    public static bool TargetsRootShellDocument(PathString path)
    {
        var p = path.Value ?? string.Empty;
        while (p.Contains("//"))
            p = p.Replace("//", "/");
        if (p.Length > 1 && p[^1] == '/')
            p = p[..^1];
        return p.Length == 0
            || p == "/"
            || p.Equals("/index.html", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSpaShellFallbackAllowed(HttpContext ctx)
    {
        if (!IsGet(ctx.Request.Method))
            return false;

        // MapFallbackToFile registers a RouteEndpoint at Order int.MaxValue.
        // Anything else - a real route at Order 0, or a branched-pipeline
        // endpoint (app.Map for /ws and /lighting/output) that isn't a
        // RouteEndpoint at all - is treated as a "real" matched endpoint
        // and must still authenticate.
        if (ctx.GetEndpoint() is { } endpoint &&
            (endpoint is not RouteEndpoint route || route.Order != int.MaxValue))
        {
            return false;
        }

        var accept = ctx.Request.Headers.Accept.ToString();
        if (!accept.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            return false;

        var fetchSite = ctx.Request.Headers["Sec-Fetch-Site"].ToString();
        // Android WebView (and modern Chromium-based kiosks) send
        // `Sec-Fetch-Site: cross-site` for top-level navigations to a
        // new origin even when the user originated the request - there
        // is no prior origin to compare against. Accepting that value
        // is safe for the SPA-shell GET path: it only serves index.html,
        // not API data; the SPA then has to authenticate via /pair
        // (which is loopback-only) before any sensitive call. CSRF on
        // state-changing endpoints is enforced separately by
        // `RejectsInsecureCsrf`.
        return fetchSite.Length == 0 ||
               string.Equals(fetchSite, "none", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fetchSite, "same-origin", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fetchSite, "cross-site", StringComparison.OrdinalIgnoreCase);
    }

    public static string? ExtractBearerOrQueryToken(HttpContext ctx)
    {
        var authHeader = ctx.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(authHeader) &&
            authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return authHeader["Bearer ".Length..].Trim();
        }

        var queryToken = ctx.Request.Query["token"].ToString();
        return string.IsNullOrWhiteSpace(queryToken) ? null : queryToken;
    }

    public static bool IsPanelSessionAllowed(HttpContext ctx)
    {
        // Routes opt in at registration via .AllowPanel(). The marker rides on
        // endpoint metadata so adding a new panel-reachable route is a chained
        // call at the registration site, not a central whitelist edit.
        if (ctx.GetEndpoint()?.Metadata.GetMetadata<AllowPanelAccess>() is not null)
            return true;

        // Pipeline-branched endpoints (app.Map for /ws, /lighting/output) don't
        // carry endpoint metadata, so they stay path-matched here. Anything
        // else routed through MapGet/MapPost should use .AllowPanel().
        var method = ctx.Request.Method;
        if (!IsGet(method))
            return false;

        var path = ctx.Request.Path.Value ?? string.Empty;
        return PathEquals(path, "/ws") || PathEquals(path, "/lighting/output");
    }

    /// <summary>
    /// CSRF guard for the plain-HTTP browser-pairing fallback. Random web
    /// pages on the same LAN can issue fetch() calls to
    /// <c>http://&lt;lan-ip&gt;:9400/...</c> and ride the user's
    /// browser-managed phone-session cookie, so on state-changing methods
    /// over HTTP we require the request to either come from the panel page
    /// itself (<c>Sec-Fetch-Site: same-origin</c>) or carry an explicit
    /// <c>Origin</c> matching the LAN service URL. HTTPS traffic and safe
    /// methods are unaffected.
    /// </summary>
    public static bool RejectsInsecureCsrf(HttpContext ctx)
    {
        if (ctx.Request.IsHttps)
            return false;

        var method = ctx.Request.Method;
        if (IsGet(method) ||
            string.Equals(method, HttpMethods.Head, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(method, HttpMethods.Options, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fetchSite = ctx.Request.Headers["Sec-Fetch-Site"].ToString();
        if (string.Equals(fetchSite, "same-origin", StringComparison.OrdinalIgnoreCase))
            return false;

        var origin = ctx.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin) &&
            string.Equals(origin, ExpectedHttpOrigin(ctx), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static string ExpectedHttpOrigin(HttpContext ctx)
    {
        var host = ctx.Request.Host.HasValue ? ctx.Request.Host.Value : "";
        return $"http://{host}";
    }

    private static bool PathEquals(string path, string candidate)
        => string.Equals(path, candidate, StringComparison.OrdinalIgnoreCase);

    private static bool IsGet(string method)
        => string.Equals(method, HttpMethods.Get, StringComparison.OrdinalIgnoreCase);
}
