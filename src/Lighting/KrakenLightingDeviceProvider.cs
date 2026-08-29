using System;
using System.Collections.Generic;
using Nexus.Service.Devices;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.Nzxt;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Exposes the Kraken's RGB channels as drivable zones: the pump ring and whatever RGB fan
/// chain is attached. Both are addressed per-LED, so the engine drives them like any other
/// strip. The channel list and its LED counts come from the cooler's own accessory table,
/// not from a hard-coded model map.
/// </summary>
public sealed class KrakenLightingDeviceProvider :
    ILightingDeviceProvider, ILightingFrameContributor, IDeviceStructureSource, IOpenRgbDeviceOwner
{
    private readonly KrakenHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private string _lastSignature = "";

    public KrakenLightingDeviceProvider(KrakenHub hub, IConfigStore store, Np50IdentifyTracker identify)
    {
        _hub = hub;
        _store = store;
        _identify = identify;
    }

    public bool IsConnected => _hub.IsConnected;

    // OpenRGB names this cooler "NZXT Kraken 2024 ELITE Series RGB" (NZXTHue2ControllerDetect.cpp).
    public bool OwnsOpenRgbDevice(Nexus.Service.Lighting.Rgb.RgbDevice device) =>
        _hub.IsConnected
        && device.Name is not null
        && device.Name.Contains("Kraken", StringComparison.OrdinalIgnoreCase);

    public event Action? DevicesChanged;

    public void OnHubStateUpdated()
    {
        var sig = BuildSignature();
        if (sig == _lastSignature)
        {
            return;
        }
        _lastSignature = sig;
        try { DevicesChanged?.Invoke(); } catch { /* swallow subscriber failures */ }
    }

    private string BuildSignature()
    {
        if (!_hub.IsConnected)
        {
            return "disconnected";
        }
        var channels = _hub.Snapshot.Channels;
        var sb = new System.Text.StringBuilder(KrakenHub.DeviceId);
        foreach (var c in channels)
        {
            sb.Append('|').Append(c.AccessoryId.ToString("X2")).Append(':').Append(c.LedCount);
        }
        return sb.ToString();
    }

    private sealed record ZoneDef(string Id, string Name, string RawName, int LedCount, byte ChannelId, int Rings);

    private List<ZoneDef> BuildZoneDefs()
    {
        var defs = new List<ZoneDef>(2);
        var channels = _hub.Snapshot.Channels;
        for (int i = 0; i < channels.Count; i++)
        {
            var c = channels[i];
            string raw = i == 0 ? "Pump Ring" : "Fans";
            defs.Add(new ZoneDef(
                KrakenHub.ZoneIdForChannelIndex(i),
                $"{KrakenHub.ProductName} - {c.AccessoryName}",
                raw,
                c.LedCount,
                c.ChannelId,
                c.Rings));
        }
        return defs;
    }

    /// <summary>
    /// LEDs laid out as the hardware actually wears them: a pump ring is one circle, a
    /// fan chain is one circle per fan side by side. Falls back to a single circle when
    /// the accessory's ring count is unknown or the user has overridden the LED count,
    /// which still beats the straight line a strip would give.
    /// </summary>
    private static (float[] u, float[] v) BuildRingUv(int ledCount, int rings)
    {
        if (ledCount <= 0)
        {
            return (Array.Empty<float>(), Array.Empty<float>());
        }
        // An uneven split would put a partial circle at the end, so only trust the ring
        // count when it divides the LEDs evenly.
        var circles = rings > 0 && ledCount % rings == 0 ? rings : 1;
        var perCircle = ledCount / circles;
        var u = new float[ledCount];
        var v = new float[ledCount];
        for (var c = 0; c < circles; c++)
        {
            var centerU = (c + 0.5f) / circles;
            for (var i = 0; i < perCircle; i++)
            {
                // Start at 12 o'clock and run clockwise, which is how both the pump ring
                // and NZXT's fans are wired.
                var angle = ((i / (double)perCircle) * 2.0 * Math.PI) - (Math.PI / 2.0);
                u[(c * perCircle) + i] = centerU + ((Radius / circles) * (float)Math.Cos(angle));
                v[(c * perCircle) + i] = 0.5f + (Radius * (float)Math.Sin(angle));
            }
        }
        return (u, v);
    }

    /// <summary>Circle radius in normalised frame space; leaves a small margin at the edge.</summary>
    private const float Radius = 0.42f;

    public GetLightingDevicesResponse GetAll()
    {
        var resp = new GetLightingDevicesResponse { IsInit = true };
        if (!_hub.IsConnected)
        {
            return resp;
        }
        var settings = _store.Load();
        var disabled = settings.Devices.DisabledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;
        var layouts = settings.Lighting.DeviceLayouts;
        var counts = settings.Devices.ZoneLedCounts;

        var defs = BuildZoneDefs();
        for (int i = 0; i < defs.Count; i++)
        {
            resp.Devices.Add(BuildZone(defs[i], i, disabled, prefs, layouts, counts));
        }
        return resp;
    }

    private static LightingDevice BuildZone(
        ZoneDef def, int zoneIndex,
        IReadOnlyList<string> disabled,
        IReadOnlyDictionary<string, LightingDevicePreference> prefs,
        IReadOnlyDictionary<string, DeviceLayout> layouts,
        IReadOnlyDictionary<string, int> counts)
    {
        var isOn = true;
        for (var i = 0; i < disabled.Count; i++)
        {
            if (disabled[i] == def.Id) { isOn = false; break; }
        }
        var brightness = 100;
        var hue = 0f;
        var saturation = 1f;
        if (prefs.TryGetValue(def.Id, out var pref))
        {
            brightness = pref.Brightness;
            hue = pref.Hue;
            saturation = pref.Saturation;
        }
        var effectiveLedCount = EffectiveLedCount(def, counts);
        var (defX, defY, defW, defH) = DefaultKrakenLayout(zoneIndex, CirclesFor(def, effectiveLedCount));
        layouts.TryGetValue(def.Id, out var layout);
        return new LightingDevice
        {
            Id = def.Id,
            Name = def.Name,
            Type = "ledstrip",
            IconType = "cooler",
            LedsOn = isOn,
            Brightness = brightness,
            Hue = hue,
            Saturation = saturation,
            LedCount = effectiveLedCount,
            CanvasX = layout?.X ?? defX,
            CanvasY = layout?.Y ?? defY,
            CanvasW = layout?.W ?? defW,
            CanvasH = layout?.H ?? defH,
            CanvasRotation = ((((layout?.Rotation ?? 0) % 360) + 360) % 360),
            ParentDeviceId = KrakenHub.DeviceId,
            ZoneIndex = zoneIndex,
            ZoneType = "linear",
            ZoneResizable = IsCountUnknown(def),
        };
    }

    /// <summary>
    /// The accessory table gives the exact LED count for every accessory we have measured,
    /// so those zones are fixed and the editor says so. Only an accessory we cannot size
    /// stays hand-editable - otherwise a stray edit silently drives the wrong length.
    /// </summary>
    private static bool IsCountUnknown(ZoneDef def) => def.LedCount <= 0;

    /// <summary>
    /// The accessory table wins whenever it knows the accessory: a hand-set count left
    /// over from before we could size the chain would otherwise keep driving the wrong
    /// length forever. Only an unmeasured accessory reads the user's override.
    /// </summary>
    private static int EffectiveLedCount(ZoneDef def, IReadOnlyDictionary<string, int> counts) =>
        IsCountUnknown(def) && counts.TryGetValue(def.Id, out var persisted)
            ? Math.Max(0, persisted)
            : def.LedCount;

    private static int CirclesFor(ZoneDef def, int effectiveLedCount) =>
        def.Rings > 0 && effectiveLedCount > 0 && effectiveLedCount % def.Rings == 0 ? def.Rings : 1;

    public void SetDisabled(IReadOnlyList<string> ids) => _store.Update(s =>
        s.Devices.DisabledLightingDevices = new List<string>(ids));

    public void SetPower(string id, bool on) => _store.Update(s =>
    {
        var current = s.Devices.DisabledLightingDevices;
        if (on)
        {
            if (!current.Contains(id)) return;
            var next = new List<string>(current.Count);
            foreach (var x in current) if (x != id) next.Add(x);
            s.Devices.DisabledLightingDevices = next;
        }
        else
        {
            if (current.Contains(id)) return;
            var next = new List<string>(current.Count + 1);
            next.AddRange(current);
            next.Add(id);
            s.Devices.DisabledLightingDevices = next;
        }
    });

    public void SetBrightness(string id, int brightness) => _store.Update(s =>
    {
        if (!s.Devices.LightingDevicePrefs.TryGetValue(id, out var pref))
        { pref = new LightingDevicePreference(); s.Devices.LightingDevicePrefs[id] = pref; }
        pref.Brightness = Math.Clamp(brightness, 0, 100);
    });

    public void SetHue(string id, float hue) => _store.Update(s =>
    {
        if (!s.Devices.LightingDevicePrefs.TryGetValue(id, out var pref))
        { pref = new LightingDevicePreference(); s.Devices.LightingDevicePrefs[id] = pref; }
        pref.Hue = hue;
    });

    public void SetSaturation(string id, float saturation) => _store.Update(s =>
    {
        if (!s.Devices.LightingDevicePrefs.TryGetValue(id, out var pref))
        { pref = new LightingDevicePreference(); s.Devices.LightingDevicePrefs[id] = pref; }
        pref.Saturation = saturation;
    });

    public void SetZoneLedCount(string id, int count)
    {
        if (count < 0 || count > KrakenProtocol.MaxDirectColors)
        {
            return;
        }
        _store.Update(s => s.Devices.ZoneLedCounts[id] = count);
    }

    public void Identify(string id, int durationMs) => _identify.Schedule(id, durationMs);

    // ── IDeviceStructureSource ──

    public IReadOnlyList<DeviceStructure> GetStructures()
    {
        if (!_hub.IsConnected)
        {
            return Array.Empty<DeviceStructure>();
        }
        var counts = _store.Load().Devices.ZoneLedCounts;
        var defs = BuildZoneDefs();
        var result = new List<DeviceStructure>(defs.Count);
        foreach (var def in defs)
        {
            var ledCount = EffectiveLedCount(def, counts);
            var (u, v) = BuildRingUv(ledCount, def.Rings);
            var structure = new DeviceStructure { DeviceId = def.Id, Name = def.Name, Partitionable = false };
            structure.Segments.Add(new StructureSegment
            {
                Index = 0,
                Name = def.RawName,
                LedCount = ledCount,
                FrameLedCount = ledCount,
                Resizable = IsCountUnknown(def),
                ZoneType = "linear",
                DefaultU = u,
                DefaultV = v,
            });
            structure.DefaultZones.Add(new DefaultZoneDef
            {
                Id = def.Id,
                Name = def.Name,
                RawName = def.RawName,
                LegacyZoneIndex = -1,
                Slices = { new ZoneSlice { Segment = 0, Start = 0, Count = ledCount } },
            });
            result.Add(structure);
        }
        return result;
    }

    // Reused across the bridge refresh so the writer never sees a fresh zero-filled frame
    // for one tick and blanks the cooler.
    private readonly Dictionary<string, DeviceFrame> _frameCache = new();

    public IReadOnlyList<DeviceFrame> BuildFrames(int startingIndex)
    {
        if (!_hub.IsConnected)
        {
            return Array.Empty<DeviceFrame>();
        }
        var settings = _store.Load();
        var layouts = settings.Lighting.DeviceLayouts;
        var counts = settings.Devices.ZoneLedCounts;
        var defs = BuildZoneDefs();
        var frames = new List<DeviceFrame>(defs.Count);
        var idx = startingIndex;

        for (int i = 0; i < defs.Count; i++)
        {
            frames.Add(BuildOrReuseFrame(defs[i], i, layouts, counts, ref idx));
        }

        if (_frameCache.Count > frames.Count)
        {
            var live = new HashSet<string>(frames.Count);
            foreach (var f in frames) live.Add(f.Id);
            var stale = new List<string>();
            foreach (var k in _frameCache.Keys) if (!live.Contains(k)) stale.Add(k);
            foreach (var k in stale) _frameCache.Remove(k);
        }
        return frames;
    }

    private DeviceFrame BuildOrReuseFrame(
        ZoneDef def, int zoneIndex,
        IReadOnlyDictionary<string, DeviceLayout> layouts,
        IReadOnlyDictionary<string, int> counts,
        ref int idx)
    {
        var effectiveLedCount = EffectiveLedCount(def, counts);
        var (defX, defY, defW, defH) = DefaultKrakenLayout(zoneIndex, CirclesFor(def, effectiveLedCount));
        layouts.TryGetValue(def.Id, out var layout);
        var rot = ((((layout?.Rotation ?? 0) % 360) + 360) % 360);
        var thisIdx = idx++;

        if (_frameCache.TryGetValue(def.Id, out var existing)
            && existing.Index == thisIdx
            && existing.LedCount == effectiveLedCount)
        {
            existing.X = layout?.X ?? defX;
            existing.Y = layout?.Y ?? defY;
            existing.W = layout?.W ?? defW;
            existing.H = layout?.H ?? defH;
            existing.Rotation = rot;
            return existing;
        }

        var frame = new DeviceFrame(
            index: thisIdx, id: def.Id, ledCount: effectiveLedCount,
            x: layout?.X ?? defX, y: layout?.Y ?? defY,
            w: layout?.W ?? defW, h: layout?.H ?? defH, rotation: rot);
        _frameCache[def.Id] = frame;
        return frame;
    }

    /// <summary>
    /// Default canvas slots, placed on the free row below the MiniHub cards. The box is
    /// square per circle so the ring lands round rather than as a flat ellipse; a two-fan
    /// chain gets a box twice as wide, one square per fan.
    /// </summary>
    internal static (float x, float y, float w, float h) DefaultKrakenLayout(int slot, int circles = 1)
    {
        // Sized against the 1000x600 effect canvas: a footprint this size spans
        // enough of it that an animation varies ACROSS the LEDs. A small box
        // samples one patch of the effect, which lights every LED the same
        // colour and reads as a static tint that drifts rather than an animation.
        const float Y = 340f;
        const float Side = 200f;
        const float Gap = 40f;
        const float BaseX = 40f;
        var s = Math.Max(0, slot);
        var c = Math.Max(1, circles);
        // Slots are laid left to right, each as wide as the zone before it needs.
        var x = BaseX + (s * (Side + Gap) * 2);
        return (x, Y, Side * c, Side);
    }
}
