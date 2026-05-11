using System.Net;
using Qos.Service.Auth;
using Qos.Service.Models;
using Qos.Service.Serialization;

namespace Qos.Service.Routes;

public static class AuthRoutes
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapGet("/pair", (HttpContext ctx, TokenService tokens) =>
        {
            // Loopback only. LAN (RFC1918, link-local) is not a trust
            // boundary - anyone on the user's Wi-Fi could otherwise fetch
            // the service token permanently.
            var remote = ctx.Connection.RemoteIpAddress;
            if (remote is null || !IPAddress.IsLoopback(remote))
            {
                return Results.Json(
                    new ApiResponse { Error = true, Msg = "Pairing is only available from the loopback interface." },
                    AppJsonContext.Default.ApiResponse,
                    statusCode: 403);
            }

            return Results.Ok(new PairResponse(tokens.Token));
        });
    }
}
