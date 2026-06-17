using Nexus.Service.Devices;
using Nexus.Service.Models;
using Nexus.Service.Models.Devices;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    private static void MapLightingDevicesEndpoints(WebApplication app)
    {
        app.MapGet("/devices/lighting-devices/all", (ILightingDeviceProvider ld) =>
            ld.GetAll());

        app.MapPost("/devices/lighting-devices/layout", (SaveDeviceLayoutBody body, Nexus.Service.Persistence.IConfigStore store, Nexus.Service.Lighting.Engine.LightingEngine engine) =>
        {
            store.Update(s =>
            {
                s.Lighting.DeviceLayouts[body.Id] = new Nexus.Service.Persistence.DeviceLayout
                { X = body.X, Y = body.Y, W = body.W, H = body.H, Rotation = body.Rotation };
            });
            foreach (var dev in engine.Devices)
            {
                if (dev.Id == body.Id)
                { dev.X = body.X; dev.Y = body.Y; dev.W = body.W; dev.H = body.H; dev.Rotation = body.Rotation; break; }
            }
            return ApiResponse.Ok();
        });

        // Reset every device frame's persisted layout (canvas X/Y/W/H/rotation)
        // back to the provider-computed defaults. The lighting providers
        // re-emit defaults on the next GetAll() since DeviceLayouts is empty;
        // we also mirror those defaults into the live engine frames so running
        // effects start sampling from the new rectangles on the next tick
        // (otherwise BuildOrReuseFrame's existing-frame-wins logic would keep
        // the pre-reset coordinates in memory). Same pattern POST /layout
        // uses to push a single user-drag into the engine. The lighting-topic
        // broadcast nudges all connected SPAs to refetch.
        app.MapDelete("/devices/lighting-devices/layouts", (
            Nexus.Service.Persistence.IConfigStore store,
            Nexus.Service.Sockets.MultiplexHub hub,
            ILightingDeviceProvider lightingProvider,
            Nexus.Service.Lighting.Engine.LightingEngine engine) =>
        {
            store.Update(s =>
            {
                s.Lighting.DeviceLayouts.Clear();
            });
            var fresh = lightingProvider.GetAll();
            foreach (var freshDev in fresh.Devices)
            {
                foreach (var frame in engine.Devices)
                {
                    if (frame.Id == freshDev.Id)
                    {
                        frame.X = freshDev.CanvasX;
                        frame.Y = freshDev.CanvasY;
                        frame.W = freshDev.CanvasW;
                        frame.H = freshDev.CanvasH;
                        frame.Rotation = freshDev.CanvasRotation;
                        break;
                    }
                }
            }
            Nexus.Service.Sockets.PanelTopics.BroadcastLighting(hub);
            return ApiResponse.Ok();
        });

        app.MapPost("/devices/lighting-devices/power", (SetLightingDevicePowerBody body, ILightingDeviceProvider ld) =>
        {
            ld.SetPower(body.Id, body.On);
            return ApiResponse.Ok();
        });
        app.MapPost("/devices/lighting-devices/brightness", (SetLightingDeviceBrightness body, ILightingDeviceProvider ld) =>
        {
            ld.SetBrightness(body.Id, body.Brightness);
            return ApiResponse.Ok();
        });
        app.MapPost("/devices/lighting-devices/color", (SetLightingDeviceColor body, ILightingDeviceProvider ld) =>
        {
            ld.SetHue(body.Id, body.Hue);
            ld.SetSaturation(body.Id, body.Saturation);
            return ApiResponse.Ok();
        });

        // Motherboard ARGB zone LED count - persists and applies via OpenRGB RESIZEZONE
        app.MapPost("/devices/lighting-devices/zone-size", (SetZoneLedCountBody body, ILightingDeviceProvider ld) =>
        {
            ld.SetZoneLedCount(body.Id, body.Count);
            return ApiResponse.Ok();
        });

        // Identify a strip / zone with a unique colour pulse
        app.MapPost("/devices/lighting-devices/identify", (IdentifyLightingDeviceBody body, ILightingDeviceProvider ld) =>
        {
            ld.Identify(body.Id, body.DurationMs);
            return ApiResponse.Ok();
        });

        // Force-rescan: restart OpenRGB subprocess (only for plugins that scan once at boot)
        // Broadcasts a `lighting` topic frame so the SPA's useRgbStatus hook
        // refetches /lighting/status immediately and observes `scanning=true`
        // without waiting for an unrelated mutation. The hook's poll-while-
        // scanning loop then tracks the rescan to completion on its own.
        app.MapPost("/devices/lighting-devices/rescan", (Nexus.Service.Lighting.Rgb.RgbBridge? bridge, Nexus.Service.Sockets.MultiplexHub hub) =>
        {
            bridge?.ForceRescan();
            Nexus.Service.Sockets.PanelTopics.BroadcastLighting(hub);
            return ApiResponse.Ok();
        });

        // LED map: get resolved positions (defaults -> applied mapping ->
        // custom overrides) for ANY lighting device. Partition-backed cards
        // (keeb / OpenRGB zones) resolve through the zone topology; other
        // contributor cards (NP50, hubs, smart lights) resolve via their
        // engine frame seeded with provider defaults.
        app.MapGet("/devices/lighting-devices/{id}/led-map", (string id, bool? defaults,
            Nexus.Service.Lighting.Zones.ZoneTopology topology,
            Nexus.Service.Lighting.Mappings.MappingApplyService mappings,
            Nexus.Service.Persistence.IConfigStore store) =>
        {
            var settings = store.Load();
            // defaults=true previews the factory layout: keep persisted LED
            // counts and the partition (they describe the hardware as wired
            // and the card shape) but drop mapping + override + group layers.
            var effective = defaults == true
                ? new Nexus.Service.Persistence.NexusSettings
                {
                    Devices = new Nexus.Service.Persistence.DevicesSettings
                    {
                        ZoneLedCounts = settings.Devices.ZoneLedCounts,
                        ZonePartitions = settings.Devices.ZonePartitions,
                        DeviceAspectRatios = settings.Devices.DeviceAspectRatios,
                    },
                }
                : settings;

            var resolution = topology.ResolveCard(id, effective);
            if (resolution is null)
                return Results.Json(new LedMapResponse { Id = id }, Nexus.Service.Serialization.AppJsonContext.Default.LedMapResponse);
            var resolved = resolution.Layout;
            var device = resolution.Device;
            var globalOffset = resolved.GlobalOffset;

            if (defaults != true)
            {
                topology.ApplyToEngine(id, resolution);
            }

            var leds = new List<LedMapEntry>(resolved.LedCount);
            for (int i = 0; i < resolved.LedCount; i++)
            {
                var globalIdx = globalOffset + i;
                leds.Add(new LedMapEntry
                {
                    Index = i,
                    U = i < resolved.U.Length ? resolved.U[i] : 0f,
                    V = i < resolved.V.Length ? resolved.V[i] : 0f,
                    Name = device is not null && globalIdx < device.LedNames.Count
                        ? device.LedNames[globalIdx]
                        : $"LED {i}",
                    ZoneType = i < resolved.ZoneTypes.Length ? resolved.ZoneTypes[i] : "unknown",
                    IsCustom = resolved.CustomLeds.Contains(i),
                    Disabled = resolved.Disabled is { } flags && i < flags.Length && flags[i],
                });
            }

            var card = mappings.FindCard(id);
            return Results.Json(new LedMapResponse
            {
                Id = id,
                LedCount = resolved.LedCount,
                Leds = leds,
                HasCustomOverrides = resolved.HasUserOverrides,
                AspectRatio = resolved.AspectRatio,
                Groups = resolved.Groups,
                Applied = resolved.Applied is { } applied
                    ? new AppliedMappingSummary
                    {
                        MappingId = applied.MappingId,
                        Name = applied.Name,
                        Source = applied.Source,
                        ContentHash = applied.ContentHash,
                        AutoApplied = applied.AutoApplied,
                        AppliedAtMs = applied.AppliedAt.ToUnixTimeMilliseconds(),
                    }
                    : null,
                DeviceKey = card?.DeviceKey ?? "",
            }, Nexus.Service.Serialization.AppJsonContext.Default.LedMapResponse);
        });

        // LED map: save custom overrides (+ optional group replacement). The
        // body carries zone-local indices; they re-key through the card's
        // slices into the device's segment-local store, replacing only the
        // entries this zone covers.
        app.MapPost("/devices/lighting-devices/{id}/led-map", (string id, SaveLedMapBody body,
            Nexus.Service.Persistence.IConfigStore store,
            Nexus.Service.Lighting.Zones.ZoneTopology topology) =>
        {
            var ctx = topology.ContextFor(id, store.Load());
            store.Update(s =>
            {
                var next = CollectOverridesOutsideZone(s, ctx);
                foreach (var o in body.Overrides)
                {
                    if (ctx.TryMapToSegment(o.LedIndex, out var segment, out var local))
                    {
                        next.Add(new Nexus.Service.Persistence.SegmentLedOverride
                        { Segment = segment, LedIndex = local, U = o.U, V = o.V, Disabled = o.Disabled });
                    }
                }
                s.Devices.DeviceLedOverrides[ctx.DeviceId] = next;
                if (body.AspectRatio > 0)
                    s.Devices.DeviceAspectRatios[ctx.DeviceId] = body.AspectRatio;
                if (body.Groups is not null)
                    s.Devices.LedGroups[id] = body.Groups;
            });
            topology.RefreshCardFrame(id);
            return ApiResponse.Ok();
        });

        // LED map: reset user deltas (overrides, groups) for this card's
        // zone. The canvas aspect ratio is device-wide and shared by sibling
        // zone cards, so it stays; the device-map DELETE owns device-level
        // reset. An applied community mapping survives; reverting that is
        // the mapping DELETE.
        app.MapDelete("/devices/lighting-devices/{id}/led-map", (string id,
            Nexus.Service.Persistence.IConfigStore store,
            Nexus.Service.Lighting.Zones.ZoneTopology topology) =>
        {
            var ctx = topology.ContextFor(id, store.Load());
            store.Update(s =>
            {
                var remaining = CollectOverridesOutsideZone(s, ctx);
                if (remaining.Count > 0)
                    s.Devices.DeviceLedOverrides[ctx.DeviceId] = remaining;
                else
                    s.Devices.DeviceLedOverrides.Remove(ctx.DeviceId);
                s.Devices.LedGroups.Remove(id);
            });
            topology.RefreshCardFrame(id);
            return ApiResponse.Ok();
        });

        // LED map editor: highlight specific LEDs (white, rest dark)
        app.MapPost("/devices/lighting-devices/{id}/led-highlight", (string id, LedHighlightBody body,
            Nexus.Service.Lighting.Engine.LightingEngine engine) =>
        {
            foreach (var frame in engine.Devices)
            {
                if (frame.Id == id)
                {
                    frame.HighlightLeds = body.Indices.Count > 0 ? new HashSet<int>(body.Indices) : null;
                    break;
                }
            }
            return ApiResponse.Ok();
        });

        // LED map editor: directional test pattern
        app.MapPost("/devices/lighting-devices/{id}/led-test-pattern", (string id, LedTestPatternBody body,
            Nexus.Service.Lighting.Engine.LightingEngine engine) =>
        {
            foreach (var frame in engine.Devices)
            {
                if (frame.Id == id)
                {
                    frame.TestPattern = body.Pattern == "animation" ? null : body.Pattern;
                    frame.TestPatternStartMs = frame.TestPattern is not null ? Environment.TickCount64 : 0;
                    break;
                }
            }
            return ApiResponse.Ok();
        });

        // LED map editor: transient preview layout (draft LED count + positions, not persisted)
        app.MapPost("/devices/lighting-devices/{id}/led-preview-layout", (string id, LedPreviewLayoutBody body,
            Nexus.Service.Lighting.Engine.LightingEngine engine) =>
        {
            foreach (var frame in engine.Devices)
            {
                if (frame.Id == id)
                {
                    frame.PreviewLedCount = body.LedCount > 0 ? body.LedCount : null;
                    if (body.Leds.Count > 0)
                    {
                        var positions = new Nexus.Service.Lighting.Engine.PreviewLedPosition[body.Leds.Count];
                        for (int i = 0; i < body.Leds.Count; i++)
                        {
                            var src = body.Leds[i];
                            positions[i] = new Nexus.Service.Lighting.Engine.PreviewLedPosition
                            {
                                Index = src.Index,
                                U = src.U,
                                V = src.V,
                                Disabled = src.Disabled,
                            };
                        }
                        frame.PreviewLayout = positions;
                    }
                    else
                    {
                        frame.PreviewLayout = null;
                    }
                    break;
                }
            }
            return ApiResponse.Ok();
        });

        // LED map editor: clear all overlays
        app.MapDelete("/devices/lighting-devices/{id}/led-editor", (string id,
            Nexus.Service.Lighting.Engine.LightingEngine engine) =>
        {
            foreach (var frame in engine.Devices)
            {
                if (frame.Id == id)
                {
                    frame.HighlightLeds = null;
                    frame.TestPattern = null;
                    frame.PreviewLedCount = null;
                    frame.PreviewLayout = null;
                    break;
                }
            }
            return ApiResponse.Ok();
        });
    }
}
