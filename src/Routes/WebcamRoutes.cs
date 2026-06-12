using Nexus.Service.Auth;
using Nexus.Service.Models;
using Nexus.Service.Models.Webcam;
using Nexus.Service.Serialization;
using Nexus.Service.Webcam;

namespace Nexus.Service.Routes;

/// <summary>
/// Phone-as-webcam control surface. LAN/direct only: every endpoint (and the
/// /webcam/stream socket in WebSocketRoutes) refuses trusted relay dispatches,
/// and the webcam paths are not in RelayHttpAllowlist. Lifecycle is
/// viewer-driven: the virtual camera is created on start and torn down on stop
/// or shortly after the stream disconnects.
/// </summary>
public static class WebcamRoutes
{
    public static void MapWebcamEndpoints(this WebApplication app)
    {
        app.MapPost("/webcam/start", async (WebcamStartRequest body, HttpContext ctx, WebcamSessionManager manager) =>
        {
            if (WebcamRelayGuard.Refuse(ctx) is { } refused)
                return refused;
            var (status, error) = await manager.StartAsync(body, ctx.RequestAborted);
            return error is null
                ? Results.Json(status, AppJsonContext.Default.WebcamStatusResponse)
                : Results.Json(ApiResponse.Fail(error), AppJsonContext.Default.ApiResponse,
                    statusCode: StatusCodes.Status400BadRequest);
        }).AllowPanel();

        app.MapPost("/webcam/stop", async (HttpContext ctx, WebcamSessionManager manager) =>
        {
            if (WebcamRelayGuard.Refuse(ctx) is { } refused)
                return refused;
            return Results.Json(await manager.StopAsync(), AppJsonContext.Default.WebcamStatusResponse);
        }).AllowPanel();

        app.MapGet("/webcam/status", (HttpContext ctx, WebcamSessionManager manager) =>
        {
            if (WebcamRelayGuard.Refuse(ctx) is { } refused)
                return refused;
            return Results.Json(manager.GetStatus(), AppJsonContext.Default.WebcamStatusResponse);
        }).AllowPanel();
    }
}
