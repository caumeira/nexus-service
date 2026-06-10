using Microsoft.AspNetCore.Http;

namespace Nexus.Service.Auth;

/// <summary>
/// Desktop-token check for mutating endpoints that panel surfaces must not
/// reach (Bearer header or ?token=, validated against the /pair token).
/// </summary>
public static class ServiceTokenRequests
{
    public static bool HasServiceToken(HttpContext ctx, TokenService tokens)
    {
        var authHeader = ctx.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", System.StringComparison.OrdinalIgnoreCase))
            return tokens.Validate(authHeader.Substring("Bearer ".Length).Trim());

        var queryToken = ctx.Request.Query["token"].ToString();
        return tokens.Validate(queryToken);
    }
}
