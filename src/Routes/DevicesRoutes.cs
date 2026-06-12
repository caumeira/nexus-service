
namespace Nexus.Service.Routes;

/// <summary>
/// Devices route group entrypoint. The actual route registrations live in
/// three partial files:
/// - DeviceListingRoutes: /devices/all, /devices/usb/all
/// - DeviceSettingsRoutes: connected check, firmware version, CNVS, mobo
///   LEDs, updates, function-check
/// - LightingDevicesRoutes: /devices/lighting-devices/* (CRUD, layout,
///   power, brightness, hue, saturation, zone-size, identify, rescan,
///   LED map editor)
/// </summary>
public static partial class DevicesRoutes
{
    public static void MapDevicesEndpoints(this WebApplication app)
    {
        MapDeviceListingEndpoints(app);
        MapDeviceSettingsEndpoints(app);
        MapLightingDevicesEndpoints(app);
        MapMappingEndpoints(app);
        MapSmartLightsEndpoints(app);
        MapNp50Endpoints(app);
        MapSmartHubEndpoints(app);
        MapMiniHubEndpoints(app);
        MapFirmwareEndpoints(app);
    }

    /// <summary>
    /// Re-resolve the full layout stack (defaults -> applied mapping -> user
    /// deltas) for any lighting device and push it into the live engine
    /// frame. OpenRGB ids resolve through the bridge; contributor cards
    /// (NP50, hubs, smart lights) resolve through their engine frame seeded
    /// with provider defaults.
    /// </summary>
    private static void RefreshEngineLedMap(string id,
        Nexus.Service.Lighting.Engine.LightingEngine engine,
        Nexus.Service.Lighting.Rgb.RgbBridge? bridge,
        Nexus.Service.Lighting.Mappings.ContributorFrameLayouts contributorLayouts,
        Nexus.Service.Persistence.IConfigStore store)
    {
        var settings = store.Load();
        Nexus.Service.Lighting.Mappings.ResolvedLedLayout? resolved = null;
        if (bridge is not null)
        {
            var (device, zoneIdx) = Nexus.Service.Lighting.Rgb.OpenRgbResolver.Resolve(id, bridge.Devices);
            if (device is not null)
                resolved = Nexus.Service.Lighting.Mappings.LedLayoutResolver.ResolveOpenRgb(device, zoneIdx, id, settings);
        }
        foreach (var frame in engine.Devices)
        {
            if (frame.Id != id)
                continue;
            if (resolved is not null)
            {
                // OpenRGB frame: untracked, plain application.
                Nexus.Service.Lighting.Mappings.LedLayoutResolver.ApplyToFrame(frame, resolved);
            }
            else
            {
                // Contributor frame: must go through the tracker so the
                // write is not later mistaken for provider defaults.
                var (defU, defV) = contributorLayouts.GetDefaults(id);
                var seeded = Nexus.Service.Lighting.Mappings.LedLayoutResolver.ResolveSeeded(
                    id, frame.LedCount, defU, defV, settings);
                contributorLayouts.Apply(frame, seeded);
            }
            break;
        }
    }
}
