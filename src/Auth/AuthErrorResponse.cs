using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Nexus.Service.Auth;

/// <summary>
/// Writes 401/403 responses in a content-negotiated form: HTML for browser
/// navigations (Accept: text/html), JSON for everything else. HTML keeps an
/// iframe or direct browser nav into a gated route from rendering raw
/// <c>{"error":true,"msg":"Unauthorized"}</c> JSON inside the panel surface.
/// </summary>
public static class AuthErrorResponse
{
    /// <summary>
    /// Writes the response. <paramref name="code"/> must be a short ASCII
    /// token (e.g. "Unauthorized", "Forbidden", "CSRF"); it's interpolated
    /// into the JSON body without escaping so any value containing quotes,
    /// backslashes, or control characters would break the JSON. The HTML
    /// path encodes both <paramref name="code"/> and <paramref name="detail"/>
    /// safely.
    /// </summary>
    public static async Task WriteAsync(HttpContext ctx, int status, string code, string detail)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.Headers.CacheControl = "no-store";

        if (PrefersHtml(ctx))
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync(BuildHtml(status, code, detail));
            return;
        }

        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync($"{{\"error\":true,\"msg\":\"{code}\"}}");
    }

    private static bool PrefersHtml(HttpContext ctx)
    {
        if (!HttpMethods.IsGet(ctx.Request.Method))
            return false;
        var accept = ctx.Request.Headers.Accept.ToString();
        return accept.Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildHtml(int status, string code, string detail)
    {
        // Self-contained: zero external assets so it renders even when the
        // network blocks /assets/* under the same auth wall.
        var safeCode = WebUtility.HtmlEncode(code);
        var safeDetail = WebUtility.HtmlEncode(detail);
        return "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">"
            + "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1,viewport-fit=cover\">"
            + $"<title>{safeCode} - Nexus</title>"
            + "<style>"
            + "html,body{margin:0;height:100%;background:#0f0f0f;color:#e6e6e6;font-family:system-ui,-apple-system,Segoe UI,sans-serif;-webkit-font-smoothing:antialiased}"
            + "body{display:flex;align-items:center;justify-content:center;padding:24px;box-sizing:border-box}"
            + ".card{max-width:360px;width:100%;text-align:center}"
            + ".badge{display:inline-block;padding:4px 10px;border:1px solid #2a2a2a;border-radius:999px;font:600 11px/1 system-ui,sans-serif;letter-spacing:.08em;text-transform:uppercase;color:#9a9a9a}"
            + ".code{margin:18px 0 6px;font:800 56px/1 system-ui,sans-serif;letter-spacing:-.02em}"
            + ".title{font:700 18px/1.3 system-ui,sans-serif;margin:0 0 8px}"
            + ".detail{font:500 14px/1.45 system-ui,sans-serif;color:#9a9a9a;margin:0 0 22px}"
            + ".retry{appearance:none;background:#1f1f1f;color:#e6e6e6;border:1px solid #303030;border-radius:10px;padding:10px 16px;font:600 13px/1 system-ui,sans-serif;cursor:pointer}"
            + ".retry:hover{background:#2a2a2a;border-color:#3a3a3a}"
            + "@media (prefers-color-scheme: light){html,body{background:#fafafa;color:#1a1a1a}.badge{border-color:#e5e5e5;color:#666}.detail{color:#666}.retry{background:#f0f0f0;color:#1a1a1a;border-color:#d4d4d4}.retry:hover{background:#e8e8e8;border-color:#bcbcbc}}"
            + "</style></head><body>"
            + $"<div class=\"card\"><div class=\"badge\">Nexus</div>"
            + $"<div class=\"code\">{status}</div>"
            + $"<p class=\"title\">{safeCode}</p>"
            + $"<p class=\"detail\">{safeDetail}</p>"
            + "<button class=\"retry\" onclick=\"location.reload()\">Try again</button>"
            + "</div></body></html>";
    }
}
