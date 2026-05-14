using System;
using System.Collections.Generic;
using Qos.Service.Devices;
using Qos.Service.Models.Devices;
using Qos.Service.Persistence;

namespace Qos.Service.Lighting.Rgb;

/// <summary>
/// Real <see cref="ILightingDeviceProvider"/> backed by the OpenRGB SDK via the
/// <see cref="RgbBridge"/>. Returns whatever devices the bundled OpenRGB-headless
/// subprocess has detected.
///
/// Per-device brightness/hue/saturation persistence still goes to the config store
/// - same shape as the stub - so the UI can store user preferences even when no
/// effect is currently running.
///
/// Motherboards with multiple ARGB headers are split into one <see cref="LightingDevice"/>
/// per zone. Each zone card carries its own id ("openrgb-N-Z"), power/brightness/hue
/// prefs, canvas rect, and configurable LED count. ARGB is a one-way protocol so
/// the LED count per header cannot be auto-detected - users set it via the zone
/// card and we persist + re-apply it via OpenRGB's RESIZEZONE opcode.
/// </summary>
public sealed class OpenRgbLightingDeviceProvider : ILightingDeviceProvider
{
    private readonly RgbBridge _bridge;
    private readonly IConfigStore _store;

    public OpenRgbLightingDeviceProvider(RgbBridge bridge, IConfigStore store)
    {
        _bridge = bridge;
        _store = store;
    }

    public bool IsConnected => _bridge.IsConnected;

    public GetLightingDevicesResponse GetAll()
    {
        var devices = _bridge.Devices;
        var settings = _store.Load();
        var disabled = settings.Devices.DisabledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;
        var layouts = settings.Lighting.DeviceLayouts;
        var zoneLedCounts = settings.Devices.ZoneLedCounts;

        var result = new List<LightingDevice>(devices.Count);
        // Two independent slot counters so device cards and motherboard zone strips
        // get their own non-overlapping grids on the canvas (cards top-left, strips
        // along the bottom). The sizes below are doubled from v1 so devices read as
        // "equipment boxes" rather than tiny tiles.
        int cardSlot = 0;
        int stripSlot = 0;
        for (int i = 0; i < devices.Count; i++)
        {
            var d = devices[i];
            var baseId = d.StableId;
            var isSplitMotherboard = d.Type == 0 && d.Zones.Count > 1;

            if (!isSplitMotherboard)
            {
                prefs.TryGetValue(baseId, out var pref);
                layouts.TryGetValue(baseId, out var layout);
                var (dx, dy, dw, dh) = DefaultCardLayout(cardSlot);
                result.Add(new LightingDevice
                {
                    Id = baseId,
                    Name = d.Name,
                    Type = OpenRgbTypeName(d.Type),
                    IconType = OpenRgbTypeName(d.Type),
                    LedsOn = !disabled.Contains(baseId),
                    Brightness = pref?.Brightness ?? 100,
                    Hue = pref?.Hue ?? 0,
                    Saturation = pref?.Saturation ?? 1.0f,
                    LedCount = d.LedCount,
                    CanvasX = layout?.X ?? dx,
                    CanvasY = layout?.Y ?? dy,
                    CanvasW = layout?.W ?? dw,
                    CanvasH = layout?.H ?? dh,
                    CanvasRotation = NormalizeRotation(layout?.Rotation ?? 0),
                });
                cardSlot++;
                continue;
            }

            // Motherboard with more than one zone - emit one card per zone.
            for (int z = 0; z < d.Zones.Count; z++)
            {
                var zone = d.Zones[z];
                var zoneId = $"{baseId}-{z}";
                prefs.TryGetValue(zoneId, out var pref);
                layouts.TryGetValue(zoneId, out var layout);
                // Trust the user's persisted choice over OpenRGB's reported count.
                // 12V RGB headers ignore ResizeZone (physically one voltage line),
                // so OpenRGB keeps reporting 1 even after we persist 60. Showing
                // the user's intended value keeps the UI stable and means the
                // LED-count editor actually "sticks" visually.
                var effectiveLedCount = zoneLedCounts.TryGetValue(zoneId, out var persistedCount)
                    ? persistedCount
                    : zone.LedCount;
                var (sx, sy, sw, sh) = DefaultStripLayout(stripSlot);
                result.Add(new LightingDevice
                {
                    Id = zoneId,
                    Name = BuildZoneName(d.Name, zone.Name, z),
                    Type = OpenRgbTypeName(d.Type),
                    IconType = OpenRgbTypeName(d.Type),
                    LedsOn = !disabled.Contains(zoneId),
                    Brightness = pref?.Brightness ?? 100,
                    Hue = pref?.Hue ?? 0,
                    Saturation = pref?.Saturation ?? 1.0f,
                    LedCount = effectiveLedCount,
                    CanvasX = layout?.X ?? sx,
                    CanvasY = layout?.Y ?? sy,
                    CanvasW = layout?.W ?? sw,
                    CanvasH = layout?.H ?? sh,
                    CanvasRotation = NormalizeRotation(layout?.Rotation ?? 0),
                    ParentDeviceId = baseId,
                    ZoneIndex = z,
                    ZoneType = ZoneTypeName(zone.ZoneType),
                    ZoneResizable = IsZoneResizable(zone.ZoneType),
                });
                stripSlot++;
            }
        }

        return new GetLightingDevicesResponse
        {
            IsInit = _bridge.IsConnected,
            Devices = result,
        };
    }

    public void SetDisabled(IReadOnlyList<string> ids) => _store.Update(s =>
    {
        s.Devices.DisabledLightingDevices = new List<string>(ids);
    });

    // Replaces the list reference rather than mutating in place so the 30fps
    // RgbBridge.OnFrame reader never observes a torn state.
    public void SetPower(string id, bool on) => _store.Update(s =>
    {
        var current = s.Devices.DisabledLightingDevices;
        if (on)
        {
            if (!current.Contains(id))
                return;
            var next = new List<string>(current.Count);
            foreach (var x in current)
            { if (x != id) next.Add(x); }
            s.Devices.DisabledLightingDevices = next;
        }
        else
        {
            if (current.Contains(id))
                return;
            var next = new List<string>(current.Count + 1);
            next.AddRange(current);
            next.Add(id);
            s.Devices.DisabledLightingDevices = next;
        }
    });

    public void SetBrightness(string id, int brightness) => _store.Update(s =>
    {
        if (!s.Devices.LightingDevicePrefs.TryGetValue(id, out var pref))
        {
            pref = new LightingDevicePreference();
            s.Devices.LightingDevicePrefs[id] = pref;
        }
        pref.Brightness = brightness;
    });

    public void SetHue(string id, float hue) => _store.Update(s =>
    {
        if (!s.Devices.LightingDevicePrefs.TryGetValue(id, out var pref))
        {
            pref = new LightingDevicePreference();
            s.Devices.LightingDevicePrefs[id] = pref;
        }
        pref.Hue = hue;
    });

    public void SetSaturation(string id, float saturation) => _store.Update(s =>
    {
        if (!s.Devices.LightingDevicePrefs.TryGetValue(id, out var pref))
        {
            pref = new LightingDevicePreference();
            s.Devices.LightingDevicePrefs[id] = pref;
        }
        pref.Saturation = saturation;
    });

    public void SetZoneLedCount(string id, int count)
    {
        if (count < 0 || count > 1024)
            return;
        if (!TryResolveZone(id, out var physIdx, out var zoneIdx))
            return;

        _store.Update(s =>
        {
            s.Devices.ZoneLedCounts[id] = count;
            if (s.Devices.LedMapOverrides.TryGetValue(id, out var list))
            {
                var pruned = new List<LedPositionOverride>(list.Count);
                foreach (var o in list)
                { if (o.LedIndex < count) pruned.Add(o); }
                if (pruned.Count != list.Count)
                    s.Devices.LedMapOverrides[id] = pruned;
            }
        });

        _bridge.RequestZoneResize(physIdx, zoneIdx, count);
    }

    public void Identify(string id, int durationMs)
    {
        _bridge.BeginIdentify(id, durationMs);
    }

    /// <summary>
    /// Default on-canvas rectangle for a full device card. Twice the v1 size +
    /// arranged in a 3-column grid so multiple devices don't overlap. Canvas
    /// coords are 1000x600 internal units; the UI rescales.
    /// </summary>
    internal static (float x, float y, float w, float h) DefaultCardLayout(int slot)
    {
        const float W = 240f;
        const float H = 60f;
        const int Cols = 3;
        const float ColGap = 320f;
        const float RowGap = 90f;
        var col = slot % Cols;
        var row = slot / Cols;
        return (30f + col * ColGap, 40f + row * RowGap, W, H);
    }

    /// <summary>
    /// Default on-canvas rectangle for a motherboard ARGB strip zone. Twice the
    /// v1 height + a 2-column grid anchored below the device-card area so
    /// strips don't pile on top of each other or overlap the cards.
    /// </summary>
    internal static (float x, float y, float w, float h) DefaultStripLayout(int slot)
    {
        const float W = 360f;
        const float H = 60f;
        const int Cols = 2;
        const float ColGap = 480f;
        const float RowGap = 70f;
        // Strips are anchored in the lower third so device cards (which go
        // top-down from y=40) never collide with strip row 0.
        const float BaseY = 380f;
        var col = slot % Cols;
        var row = slot / Cols;
        return (40f + col * ColGap, BaseY + row * RowGap, W, H);
    }

    /// <summary>
    /// Resolve a zone ID to (physicalIndex, zoneIndex) by matching against the
    /// live device list. Zone IDs are "{stableId}-{zoneIndex}".
    /// </summary>
    private bool TryResolveZone(string id, out int physicalIndex, out int zoneIndex)
    {
        physicalIndex = -1;
        zoneIndex = -1;
        if (string.IsNullOrEmpty(id))
            return false;

        var devices = _bridge.Devices;
        for (int i = 0; i < devices.Count; i++)
        {
            var stableId = devices[i].StableId;
            if (id.Length > stableId.Length + 1
                && id.StartsWith(stableId, StringComparison.Ordinal)
                && id[stableId.Length] == '-'
                && int.TryParse(id.AsSpan(stableId.Length + 1), out zoneIndex)
                && zoneIndex >= 0)
            {
                physicalIndex = devices[i].Index;
                return true;
            }
        }
        return false;
    }

    private static string BuildZoneName(string deviceName, string zoneName, int zoneIndex)
    {
        if (!string.IsNullOrWhiteSpace(zoneName))
        {
            return $"{deviceName} - {zoneName}";
        }
        return $"{deviceName} - Zone {zoneIndex + 1}";
    }

    private static string ZoneTypeName(uint t) => t switch
    {
        0 => "single",
        1 => "linear",
        2 => "matrix",
        _ => "unknown",
    };

    /// <summary>
    /// Single (12V RGB) and Linear (5V ARGB) zones are both user-resizable. For a
    /// 5V ARGB header the count is the addressable LED chain length. For a 12V RGB
    /// header the header only outputs one colour but the user can still tell us
    /// "there are 60 LEDs physically on that strip" so the count lines up with the
    /// other cards. Matrix zones (keyboard grids) have a fixed layout and stay
    /// non-resizable.
    /// </summary>
    private static bool IsZoneResizable(uint zoneType) => zoneType == 0 || zoneType == 1;

    /// <summary>
    /// Clamp persisted rotation values to the four valid quarter-turns. Older builds
    /// wrote 80 as the default (DTO bug), which this normalises to 0 on load so the
    /// UI and engine never see a nonsense angle.
    /// </summary>
    private static int NormalizeRotation(int rotation)
    {
        var r = ((rotation % 360) + 360) % 360;
        return r switch
        {
            90 => 90,
            180 => 180,
            270 => 270,
            _ => 0,
        };
    }

    /// <summary>
    /// OpenRGB device type enum -> human-readable string. Mirrors the names in
    /// OpenRGB's RGBController/RGBController.h DEVICE_TYPE enum.
    /// </summary>
    private static string OpenRgbTypeName(uint type) => type switch
    {
        0 => "motherboard",
        1 => "dram",
        2 => "gpu",
        3 => "cooler",
        4 => "ledstrip",
        5 => "keyboard",
        6 => "mouse",
        7 => "mousemat",
        8 => "headset",
        9 => "headset_stand",
        10 => "gamepad",
        11 => "light",
        12 => "speaker",
        13 => "virtual",
        14 => "storage",
        15 => "case",
        16 => "microphone",
        17 => "accessory",
        _ => "unknown",
    };
}
