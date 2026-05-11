using Qos.Service.Devices;
using Qos.Service.Models;
using Qos.Service.Models.Devices;

namespace Qos.Service.Routes;

public static partial class DevicesRoutes
{
    private static void MapLightingDevicesEndpoints(WebApplication app)
    {
        app.MapGet("/devices/lighting-devices/all", (ILightingDeviceProvider ld) =>
            ld.GetAll());

        app.MapPost("/devices/lighting-devices/layout", (SaveDeviceLayoutBody body, Qos.Service.Persistence.IConfigStore store, Qos.Service.Lighting.Engine.LightingEngine engine) =>
        {
            store.Update(s =>
            {
                s.Lighting.DeviceLayouts[body.Id] = new Qos.Service.Persistence.DeviceLayout
                { X = body.X, Y = body.Y, W = body.W, H = body.H, Rotation = body.Rotation };
            });
            foreach (var dev in engine.Devices)
            {
                if (dev.Id == body.Id)
                { dev.X = body.X; dev.Y = body.Y; dev.W = body.W; dev.H = body.H; dev.Rotation = body.Rotation; break; }
            }
            return ApiResponse.Ok();
        });

        app.MapPost("/devices/lighting-devices/disable", (SetDisabledLedsBody body, ILightingDeviceProvider ld) =>
        {
            ld.SetDisabled(body.Devices);
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
        app.MapPost("/devices/lighting-devices/hue", (SetLightingDeviceHue body, ILightingDeviceProvider ld) =>
        {
            ld.SetHue(body.Id, body.Hue);
            return ApiResponse.Ok();
        });
        app.MapPost("/devices/lighting-devices/saturation", (SetLightingDeviceSaturation body, ILightingDeviceProvider ld) =>
        {
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
        app.MapPost("/devices/lighting-devices/rescan", (Qos.Service.Lighting.Rgb.RgbBridge? bridge) =>
        {
            bridge?.ForceRescan();
            return ApiResponse.Ok();
        });

        // LED map: get resolved positions (defaults + custom overrides)
        app.MapGet("/devices/lighting-devices/{id}/led-map", (string id, bool? defaults,
            Qos.Service.Lighting.Rgb.RgbBridge? bridge,
            Qos.Service.Lighting.Engine.LightingEngine engine,
            Qos.Service.Persistence.IConfigStore store) =>
        {
            if (bridge is null)
                return Results.Json(new LedMapResponse { Id = id }, Qos.Service.Serialization.AppJsonContext.Default.LedMapResponse);

            var (device, zoneIdx) = ResolveDevice(id, bridge.Devices);
            if (device is null)
                return Results.Json(new LedMapResponse { Id = id }, Qos.Service.Serialization.AppJsonContext.Default.LedMapResponse);

            float[] defU, defV;
            int ledCount;
            string[] zoneTypes;
            int globalOffset;
            if (zoneIdx >= 0 && zoneIdx < device.Zones.Count)
            {
                var zone = device.Zones[zoneIdx];
                ledCount = zone.LedCount;
                globalOffset = 0;
                for (int z = 0; z < zoneIdx; z++)
                    globalOffset += device.Zones[z].LedCount;
                defU = new float[ledCount];
                defV = new float[ledCount];
                for (int i = 0; i < ledCount; i++)
                {
                    defU[i] = ledCount > 1 ? (float)i / (ledCount - 1) : 0.5f;
                    defV[i] = 0.5f;
                }
                zoneTypes = new string[ledCount];
                var zt = zone.ZoneType switch { 0 => "single", 1 => "linear", 2 => "matrix", _ => "unknown" };
                for (int i = 0; i < ledCount; i++)
                    zoneTypes[i] = zt;
            }
            else
            {
                var (dU, dV) = Qos.Service.Lighting.Rgb.LedUvComputer.ComputeDefaults(device);
                defU = dU;
                defV = dV;
                ledCount = device.LedCount;
                zoneTypes = BuildZoneTypeMap(device);
                globalOffset = 0;
                if (defU.Length == 0 && ledCount > 0)
                {
                    defU = new float[ledCount];
                    defV = new float[ledCount];
                    for (int i = 0; i < ledCount; i++)
                    {
                        defU[i] = ledCount > 1 ? (float)i / (ledCount - 1) : 0.5f;
                        defV[i] = 0.5f;
                    }
                }
            }

            var settings = store.Load();
            var customSet = new HashSet<int>();
            var disabledSet = new HashSet<int>();
            if (defaults != true)
            {
                var overrides = settings.Devices.LedMapOverrides;
                overrides.TryGetValue(id, out var customList);
                if (customList is not null)
                {
                    foreach (var o in customList)
                    {
                        if (o.LedIndex >= 0 && o.LedIndex < ledCount)
                        {
                            customSet.Add(o.LedIndex);
                            if (defU.Length > o.LedIndex)
                            {
                                defU[o.LedIndex] = o.U;
                                defV[o.LedIndex] = o.V;
                            }
                            if (o.Disabled)
                                disabledSet.Add(o.LedIndex);
                        }
                    }
                }
                if (defU.Length > 0)
                {
                    foreach (var frame in engine.Devices)
                    {
                        if (frame.Id == id)
                        { frame.LedV = defV; frame.LedU = defU; break; }
                    }
                }
            }

            var leds = new List<LedMapEntry>(ledCount);
            for (int i = 0; i < ledCount; i++)
            {
                var globalIdx = globalOffset + i;
                leds.Add(new LedMapEntry
                {
                    Index = i,
                    U = i < defU.Length ? defU[i] : 0f,
                    V = i < defV.Length ? defV[i] : 0f,
                    Name = globalIdx < device.LedNames.Count ? device.LedNames[globalIdx] : $"LED {i}",
                    ZoneType = i < zoneTypes.Length ? zoneTypes[i] : "unknown",
                    IsCustom = customSet.Contains(i),
                    Disabled = disabledSet.Contains(i),
                });
            }

            settings.Devices.LedMapAspectRatios.TryGetValue(id, out var savedRatio);

            return Results.Json(new LedMapResponse
            {
                Id = id,
                LedCount = ledCount,
                Leds = leds,
                HasCustomOverrides = customSet.Count > 0,
                AspectRatio = savedRatio > 0 ? savedRatio : 0,
            }, Qos.Service.Serialization.AppJsonContext.Default.LedMapResponse);
        });

        // LED map: save custom overrides
        app.MapPost("/devices/lighting-devices/{id}/led-map", (string id, SaveLedMapBody body,
            Qos.Service.Persistence.IConfigStore store,
            Qos.Service.Lighting.Engine.LightingEngine engine,
            Qos.Service.Lighting.Rgb.RgbBridge? bridge) =>
        {
            store.Update(s =>
            {
                s.Devices.LedMapOverrides[id] = body.Overrides;
                if (body.AspectRatio > 0)
                    s.Devices.LedMapAspectRatios[id] = body.AspectRatio;
            });
            RefreshEngineLedMap(id, engine, bridge, store);
            return ApiResponse.Ok();
        });

        // LED map: reset to defaults
        app.MapDelete("/devices/lighting-devices/{id}/led-map", (string id,
            Qos.Service.Persistence.IConfigStore store,
            Qos.Service.Lighting.Engine.LightingEngine engine,
            Qos.Service.Lighting.Rgb.RgbBridge? bridge) =>
        {
            store.Update(s =>
            {
                s.Devices.LedMapOverrides.Remove(id);
                s.Devices.LedMapAspectRatios.Remove(id);
            });
            RefreshEngineLedMap(id, engine, bridge, store);
            return ApiResponse.Ok();
        });

        // LED map editor: highlight specific LEDs (white, rest dark)
        app.MapPost("/devices/lighting-devices/{id}/led-highlight", (string id, LedHighlightBody body,
            Qos.Service.Lighting.Engine.LightingEngine engine) =>
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
            Qos.Service.Lighting.Engine.LightingEngine engine) =>
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

        // LED map editor: clear all overlays
        app.MapDelete("/devices/lighting-devices/{id}/led-editor", (string id,
            Qos.Service.Lighting.Engine.LightingEngine engine) =>
        {
            foreach (var frame in engine.Devices)
            {
                if (frame.Id == id)
                {
                    frame.HighlightLeds = null;
                    frame.TestPattern = null;
                    break;
                }
            }
            return ApiResponse.Ok();
        });
    }
}
