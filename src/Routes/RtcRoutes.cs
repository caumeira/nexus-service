using Nexus.Service.Auth;
using Nexus.Service.Models;
using Nexus.Service.Rtc;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

/// <summary>
/// WebRTC DataChannel direct P2P signaling. The phone POSTs its offer here,
/// typically over the existing REST-over-relay tunnel (a paired session
/// upgrading off the relay); <c>.AllowPanel()</c> also permits a direct
/// LAN-authenticated phone-session call.
/// </summary>
public static class RtcRoutes
{
    public static void MapRtcEndpoints(this WebApplication app)
    {
        app.MapPost("/rtc/offer", async (HttpContext ctx, RtcOfferRequest body, RtcSessionManager rtc) =>
        {
            var sessionId = ctx.Items.TryGetValue(PathAuthMiddleware.PhoneSessionIdItem, out var raw) ? raw as string : null;
            if (string.IsNullOrEmpty(sessionId))
            {
                return Results.Json(
                    ApiResponse.Fail("no phone session"), AppJsonContext.Default.ApiResponse,
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var result = await rtc.HandleOfferAsync(sessionId, body, ctx.RequestAborted);
            if (!result.Ok)
            {
                return Results.Json(
                    ApiResponse.Fail(result.Error), AppJsonContext.Default.ApiResponse,
                    statusCode: StatusCodes.Status403Forbidden);
            }

            return Results.Json(new RtcAnswerResponse { Sdp = result.Sdp }, AppJsonContext.Default.RtcAnswerResponse);
        }).AllowPanel();
    }
}
