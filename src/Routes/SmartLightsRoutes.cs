using System.Threading;
using Nexus.Service.Lighting.Smart;
using Nexus.Service.Models;
using Nexus.Service.Models.SmartLights;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    /// <summary>
    /// Smart (network) light discovery + pairing + management. Per-device
    /// power/brightness/hue/saturation/identify reuse the existing
    /// /devices/lighting-devices/* routes — the composite provider routes those
    /// by id prefix, so smart lights are first-class there with no extra routes.
    /// </summary>
    private static void MapSmartLightsEndpoints(WebApplication app)
    {
        app.MapGet("/smart-lights/all", async (SmartLightProvider p, CancellationToken ct) => await p.GetSmartLightDtosAsync(ct));

        app.MapPost("/smart-lights/discover", async (DiscoverSmartLightsBody body, SmartLightProvider p, CancellationToken ct)
            => await p.DiscoverAsync(body.Brand, ct));

        app.MapPost("/smart-lights/pair", async (PairSmartLightBody body, SmartLightProvider p, Nexus.Service.Sockets.MultiplexHub hub, CancellationToken ct) =>
        {
            var result = await p.PairAsync(body, ct);
            if (result.Ok) Nexus.Service.Sockets.PanelTopics.BroadcastLighting(hub);
            return result;
        });

        app.MapPost("/smart-lights/remove", (RemoveSmartLightBody body, SmartLightProvider p, Nexus.Service.Sockets.MultiplexHub hub) =>
        {
            p.Remove(body.Id);
            Nexus.Service.Sockets.PanelTopics.BroadcastLighting(hub);
            return ApiResponse.Ok();
        });

        // Enable/disable a paired light without unpairing — it stays listed but
        // leaves the lighting canvas/effects when disabled.
        app.MapPost("/smart-lights/enable", (EnableSmartLightBody body, SmartLightProvider p, Nexus.Service.Sockets.MultiplexHub hub) =>
        {
            p.SetEnabled(body.Id, body.Enabled);
            Nexus.Service.Sockets.PanelTopics.BroadcastLighting(hub);
            return ApiResponse.Ok();
        });
    }
}
