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

    private sealed record ZoneDef(string Id, string Name, string RawName, int LedCount, byte ChannelId);

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
                c.ChannelId));
        }
        return defs;
    }

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
        var effectiveLedCount = def.LedCount;
        if (counts.TryGetValue(def.Id, out var persisted))
        {
            effectiveLedCount = Math.Max(0, persisted);
        }
        var (defX, defY, defW, defH) = DefaultKrakenLayout(zoneIndex);
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
            // The fan chain length varies by radiator size, so the count stays user-editable.
            ZoneResizable = true,
        };
    }

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
            var ledCount = counts.TryGetValue(def.Id, out var persisted) ? Math.Max(0, persisted) : def.LedCount;
            var structure = new DeviceStructure { DeviceId = def.Id, Name = def.Name, Partitionable = false };
            structure.Segments.Add(new StructureSegment
            {
                Index = 0,
                Name = def.RawName,
                LedCount = ledCount,
                FrameLedCount = ledCount,
                Resizable = true,
                ZoneType = "linear",
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
        var effectiveLedCount = def.LedCount;
        if (counts.TryGetValue(def.Id, out var persisted))
        {
            effectiveLedCount = Math.Max(0, persisted);
        }
        var (defX, defY, defW, defH) = DefaultKrakenLayout(zoneIndex);
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

    /// <summary>Default canvas slots, placed on the free row below the MiniHub cards.</summary>
    internal static (float x, float y, float w, float h) DefaultKrakenLayout(int slot)
    {
        const float Y = 366f;
        const float W = 220f;
        const float H = 60f;
        const float Gap = 240f;
        const float BaseX = 40f;
        var s = Math.Max(0, slot);
        return (BaseX + ((s % 4) * Gap), Y, W, H);
    }
}
