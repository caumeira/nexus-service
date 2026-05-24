using System.Net.WebSockets;
using Nexus.Service.Sockets;

namespace Nexus.Service.Routes;

/// <summary>
/// WebSocket endpoints. The multiplexed <c>/ws</c> endpoint handles all JSON
/// topics via dynamic subscriptions. <c>/lighting/output</c> stays separate
/// for binary 60fps RGB frame streaming.
/// </summary>
public static class WebSocketRoutes
{
    public static void MapWebSocketEndpoints(this WebApplication app)
    {
        // Multiplexed WebSocket - single endpoint with dynamic topic subscriptions.
        // ctx.Items["PhoneSessionId"] is populated by the auth middleware when
        // the request authenticated via a phone-session cookie/bearer; null
        // means a desktop/panel-kiosk client and the connection is never
        // eligible for the Pair Remote killswitch.
        app.Map("/ws", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("{\"error\":true,\"msg\":\"WebSocket expected\"}");
                return;
            }
            var phoneSessionId = ctx.Items.TryGetValue("PhoneSessionId", out var raw) ? raw as string : null;
            using WebSocket socket = await ctx.WebSockets.AcceptWebSocketAsync();
            var hub = ctx.RequestServices.GetRequiredService<MultiplexHub>();
            await hub.HandleClientAsync(socket, phoneSessionId, ctx.RequestAborted);
        });

        // Lighting output — binary 60fps frames (not multiplexed)
        app.Map("/lighting/output", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("{\"error\":true,\"msg\":\"WebSocket expected\"}");
                return;
            }
            using WebSocket socket = await ctx.WebSockets.AcceptWebSocketAsync();
            var hub = ctx.RequestServices.GetRequiredService<LightingOutputHub>();
            await hub.HandleClientAsync(socket, cancellationToken: ctx.RequestAborted);
        });
    }
}
