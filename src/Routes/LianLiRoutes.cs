using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Models;
using Nexus.Service.Peripherals.LianLi;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    private static void MapLianLiEndpoints(WebApplication app)
    {
        // GET /devices/lianli/state - connection, RPM, duty, and fan count per port.
        app.MapGet("/devices/lianli/state", (LianLiHub hub, IConfigStore store) =>
        {
            var s = store.Load();
            var state = hub.State;
            var resp = new LianLiStateResponse
            {
                IsConnected = hub.IsConnected,
                Rpm = new[] { state.Rpm[0], state.Rpm[1], state.Rpm[2], state.Rpm[3] },
                Duty = new[] { state.Duty[0], state.Duty[1], state.Duty[2], state.Duty[3] },
                FansPerPort = new[]
                {
                    s.Devices.LianLi.Port0Fans,
                    s.Devices.LianLi.Port1Fans,
                    s.Devices.LianLi.Port2Fans,
                    s.Devices.LianLi.Port3Fans,
                },
            };
            return Results.Json(resp, AppJsonContext.Default.LianLiStateResponse);
        });

        // PUT /devices/lianli/fan-count - set fan count for one port (0..3), sends
        // SetQuantity to hub and persists the count so the lighting provider
        // allocates the correct LED frames.
        app.MapPut("/devices/lianli/fan-count", (
            LianLiFanCountRequest body,
            LianLiHub hub,
            IConfigStore store,
            LianLiLightingDeviceProvider lighting) =>
        {
            if (body.Port < 0 || body.Port >= LianLiProtocol.PortCount)
            {
                return Results.BadRequest(ApiResponse.Fail("port must be 0..3"));
            }
            if (body.Count < 0 || body.Count > 4)
            {
                return Results.BadRequest(ApiResponse.Fail("count must be 0..4"));
            }
            if (hub.IsConnected)
            {
                hub.SetQuantity(body.Port, body.Count);
            }
            store.Update(s => s.Devices.LianLi.SetFans(body.Port, body.Count));
            lighting.OnHubStateUpdated();
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        // GET /devices/lianli/composition - mirror/combine state + per-port active flags.
        app.MapGet("/devices/lianli/composition", (LianLiHub hub, IConfigStore store) =>
        {
            var s = store.Load();
            var hubId = hub.DeviceId;
            var comp = LianLiZoneSupport.ReadComposition(s, hubId);
            var active = new bool[LianLiProtocol.PortCount];
            var fans = new int[LianLiProtocol.PortCount];
            for (var p = 0; p < LianLiProtocol.PortCount; p++)
            {
                fans[p] = LianLiZoneSupport.ClampFans(s.Devices.LianLi.GetFans(p));
                active[p] = fans[p] > 0;
            }
            return Results.Json(new LianLiCompositionResponse
            {
                Connected = hub.IsConnected,
                Mirror = comp.Mirror,
                CombineRings = comp.CombineRings,
                PortCount = LianLiProtocol.PortCount,
                ActivePorts = active,
                FansPerPort = fans,
            }, AppJsonContext.Default.LianLiCompositionResponse);
        });

        // PUT /devices/lianli/composition - patch mirror / combine rings / per-port
        // on-off (a port toggle maps to fan count 0 vs max). Recomposing the device
        // set drops the per-zone state of devices that disappear; a mirror/combine
        // change clears the affected devices' custom partitions so zone resolution
        // uses the new default zones (a stale partition desyncs zone ids from the cards).
        app.MapPut("/devices/lianli/composition", (
            LianLiCompositionRequest body,
            LianLiHub hub,
            IConfigStore store,
            LianLiLightingDeviceProvider lighting,
            Nexus.Service.Lighting.Rgb.RgbBridge? bridge,
            Nexus.Service.Sockets.MultiplexHub mux) =>
        {
            var hubId = hub.DeviceId;
            var oldIds = LianLiZoneSupport.ZoneIds(store.Load(), hubId);

            store.Update(s =>
            {
                var comp = LianLiZoneSupport.ReadComposition(s, hubId);
                var nextMirror = body.Mirror ?? comp.Mirror;
                var nextCombine = body.CombineRings ?? comp.CombineRings;
                var structural = nextMirror != comp.Mirror || nextCombine != comp.CombineRings;
                s.Devices.LightingComposition[hubId] = new HubCompositionSettings
                {
                    Mirror = nextMirror,
                    CombineRings = nextCombine,
                };
                if (body.Ports is not null)
                {
                    for (var p = 0; p < LianLiProtocol.PortCount && p < body.Ports.Length; p++)
                    {
                        var on = body.Ports[p];
                        var cur = LianLiZoneSupport.ClampFans(s.Devices.LianLi.GetFans(p));
                        if (on && cur == 0) s.Devices.LianLi.SetFans(p, LianLiProtocol.MaxFansPerPort);
                        else if (!on && cur > 0) s.Devices.LianLi.SetFans(p, 0);
                    }
                }

                var newIds = new HashSet<string>(LianLiZoneSupport.ZoneIds(s, hubId));
                var orphaned = new List<string>();
                foreach (var id in oldIds)
                {
                    if (!newIds.Contains(id)) orphaned.Add(id);
                }
                ZoneStateDrop.Drop(s, orphaned);

                if (structural)
                {
                    ZoneStateDrop.Drop(s, oldIds);
                    foreach (var did in LianLiZoneSupport.DeviceIds(s, hubId))
                    {
                        s.Devices.ZonePartitions.Remove(did);
                    }
                }
            });

            if (body.Ports is not null && hub.IsConnected)
            {
                var after = store.Load();
                for (var p = 0; p < LianLiProtocol.PortCount && p < body.Ports.Length; p++)
                {
                    hub.SetQuantity(p, LianLiZoneSupport.ClampFans(after.Devices.LianLi.GetFans(p)));
                }
            }

            lighting.OnHubStateUpdated();
            bridge?.RequestTopologyRefresh();
            Nexus.Service.Sockets.PanelTopics.BroadcastLighting(mux);
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });
    }
}

public sealed class LianLiStateResponse
{
    public bool IsConnected { get; set; }
    public int[] Rpm { get; set; } = Array.Empty<int>();
    public int[] Duty { get; set; } = Array.Empty<int>();
    public int[] FansPerPort { get; set; } = Array.Empty<int>();
}

public sealed class LianLiFanCountRequest
{
    public int Port { get; set; }
    public int Count { get; set; }
}

/// <summary>Shape returned by GET /devices/lianli/composition.</summary>
public sealed class LianLiCompositionResponse
{
    public bool Connected { get; set; }
    public bool Mirror { get; set; }
    public bool CombineRings { get; set; }
    public int PortCount { get; set; }
    public bool[] ActivePorts { get; set; } = Array.Empty<bool>();
    public int[] FansPerPort { get; set; } = Array.Empty<int>();
}

/// <summary>Body for PUT /devices/lianli/composition; each field is a patch (null = unchanged).</summary>
public sealed class LianLiCompositionRequest
{
    public bool? Mirror { get; set; }
    public bool? CombineRings { get; set; }
    /// <summary>Per-port on/off; index = port. True restores fans to max, false sets 0.</summary>
    public bool[]? Ports { get; set; }
}
