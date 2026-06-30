using System;
using System.Collections.Generic;
using Nexus.Service.Devices;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.Hyte.SmartHub;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Exposes the Smart Hub's four ARGB ports as drivable
/// <see cref="LightingDevice"/>s grouped under a "HYTE Smart Hub" header,
/// mirroring <see cref="MiniHubLightingDeviceProvider"/>. Same engine→writer
/// pipeline: BuildFrames contributes the zones to the engine;
/// <see cref="SmartHubLightingFrameWriter"/> pushes the rendered LED bytes to
/// the hub each tick.
///
/// The firmware does not enumerate LED counts, so every port starts at 0 LEDs
/// and the user declares the count (zones are resizable up to
/// <see cref="SmartHubProtocol.MaxLedsPerPort"/> via the lighting page).
/// </summary>
public sealed class SmartHubLightingDeviceProvider :
    ILightingDeviceProvider, ILightingFrameContributor, IDeviceStructureSource, IComposableHubSource
{
    private readonly SmartHubHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private string _lastSignature = "";

    public SmartHubLightingDeviceProvider(SmartHubHub hub, IConfigStore store, Np50IdentifyTracker identify)
    {
        _hub = hub;
        _store = store;
        _identify = identify;
    }

    public bool IsConnected => _hub.IsConnected;

    public event Action? DevicesChanged;

    public void OnHubStateUpdated()
    {
        var sig = BuildSignature();
        if (sig == _lastSignature) return;
        _lastSignature = sig;
        try { DevicesChanged?.Invoke(); } catch { /* swallow subscriber failures */ }
    }

    private string BuildSignature()
    {
        if (!_hub.IsConnected) return "disconnected";
        return _hub.DeviceId + (ReadMirror(_store.Load(), _hub.DeviceId) ? "|m" : "");
    }

    /// <summary>The four ARGB ports collapse into one mirror device when set; default off.</summary>
    internal static bool ReadMirror(NexusSettings settings, string hubId)
        => settings.Devices.LightingComposition.TryGetValue(hubId, out var c) && c is not null && c.Mirror;

    internal static string MirrorId(string hubId) => $"{hubId}:mirror";

    private static int MirrorLedCount(SmartHubHub hub, IReadOnlyDictionary<string, int> counts)
    {
        if (counts.TryGetValue(MirrorId(hub.DeviceId), out var persisted))
        {
            return Math.Max(0, persisted);
        }
        var max = 0;
        foreach (var port in hub.State.Ports)
        {
            var c = counts.TryGetValue($"{hub.DeviceId}:port{port.Channel}", out var pc)
                ? Math.Max(0, pc)
                : port.LedCount;
            if (c > max) max = c;
        }
        return max;
    }

    // ── IComposableHubSource ──

    public HubCompositionInfo? DescribeComposition(string deviceId)
    {
        var hubId = _hub.DeviceId;
        if (!_hub.IsConnected || string.IsNullOrEmpty(hubId)) return null;
        if (deviceId != hubId && !deviceId.StartsWith(hubId + ":", StringComparison.Ordinal)) return null;

        var active = new bool[SmartHubProtocol.ArgbPortCount];
        for (var i = 0; i < active.Length; i++) active[i] = true;
        return new HubCompositionInfo
        {
            HubId = hubId,
            HubKind = "smarthub",
            PortCount = SmartHubProtocol.ArgbPortCount,
            HasRingsAxis = false,
            HasPortToggle = false,
            HasMirror = true,
            Mirror = ReadMirror(_store.Load(), hubId),
            CombineRings = false,
            ActivePorts = active,
        };
    }

    public GetLightingDevicesResponse GetAll()
    {
        var resp = new GetLightingDevicesResponse { IsInit = true };
        if (!_hub.IsConnected) return resp;
        var hubId = _hub.DeviceId;
        var settings = _store.Load();
        var disabled = settings.Devices.DisabledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;
        var layouts = settings.Lighting.DeviceLayouts;
        var counts = settings.Devices.ZoneLedCounts;
        var slot = 0;

        if (ReadMirror(settings, hubId))
        {
            resp.Devices.Add(BuildZone(
                id: MirrorId(hubId), name: $"{SmartHubHub.ProductName} - All Ports (ARGB)",
                firmwareLedCount: MirrorLedCount(_hub, counts), zoneIndex: 0, parentDeviceId: hubId,
                disabled, prefs, layouts, counts));
            return resp;
        }

        foreach (var port in _hub.State.Ports)
        {
            var id = $"{hubId}:port{port.Channel}";
            resp.Devices.Add(BuildZone(
                id: id, name: $"{SmartHubHub.ProductName} - Port {port.Channel} (ARGB)",
                firmwareLedCount: port.LedCount, zoneIndex: slot++, parentDeviceId: hubId,
                disabled, prefs, layouts, counts));
        }
        return resp;
    }

    // ── IDeviceStructureSource ──

    public IReadOnlyList<DeviceStructure> GetStructures()
    {
        if (!_hub.IsConnected || string.IsNullOrEmpty(_hub.DeviceId))
        {
            return Array.Empty<DeviceStructure>();
        }
        var hubId = _hub.DeviceId;
        var settings = _store.Load();
        var counts = settings.Devices.ZoneLedCounts;

        if (ReadMirror(settings, hubId))
        {
            return new[]
            {
                BuildStructure(MirrorId(hubId), $"{SmartHubHub.ProductName} - All Ports (ARGB)",
                    "All Ports", "mirror", MirrorLedCount(_hub, counts)),
            };
        }

        var structures = new List<DeviceStructure>(_hub.State.Ports.Length);
        foreach (var port in _hub.State.Ports)
        {
            var portId = $"{hubId}:port{port.Channel}";
            var effectiveLedCount = port.LedCount;
            if (counts.TryGetValue(portId, out var persisted))
            {
                effectiveLedCount = Math.Max(0, persisted);
            }
            structures.Add(BuildStructure(portId, $"{SmartHubHub.ProductName} - Port {port.Channel} (ARGB)",
                $"Port {port.Channel}", $"port{port.Channel}", effectiveLedCount));
        }
        return structures;
    }

    private static DeviceStructure BuildStructure(string id, string name, string rawName, string keySlug, int ledCount)
    {
        var key = DeviceKeyComputer.ForFirstParty(SmartHubProtocol.VendorId, SmartHubProtocol.ProductId, keySlug);
        var structure = new DeviceStructure { DeviceId = id, Name = name, DeviceKey = key };
        structure.Segments.Add(new StructureSegment
        {
            Index = 0,
            Name = rawName,
            LedCount = ledCount,
            FrameLedCount = ledCount,
            Resizable = true,
            ZoneType = "linear",
        });
        structure.DefaultZones.Add(new DefaultZoneDef
        {
            Id = id,
            Name = name,
            RawName = rawName,
            DeviceKey = key,
            LegacyZoneIndex = -1,
            Slices = { new ZoneSlice { Segment = 0, Start = 0, Count = ledCount } },
        });
        return structure;
    }

    private static LightingDevice BuildZone(
        string id, string name, int firmwareLedCount, int zoneIndex, string parentDeviceId,
        IReadOnlyList<string> disabled,
        IReadOnlyDictionary<string, LightingDevicePreference> prefs,
        IReadOnlyDictionary<string, DeviceLayout> layouts,
        IReadOnlyDictionary<string, int> counts)
    {
        var isOn = true;
        for (var i = 0; i < disabled.Count; i++) if (disabled[i] == id) { isOn = false; break; }
        var brightness = 100;
        var hue = 0f;
        var saturation = 1f;
        if (prefs.TryGetValue(id, out var pref))
        {
            brightness = pref.Brightness; hue = pref.Hue; saturation = pref.Saturation;
        }
        var effectiveLedCount = firmwareLedCount;
        if (counts.TryGetValue(id, out var persisted))
            effectiveLedCount = Math.Max(0, persisted);
        var (defX, defY, defW, defH) = DefaultSmartHubLayout(zoneIndex);
        layouts.TryGetValue(id, out var layout);
        return new LightingDevice
        {
            Id = id, Name = name, Type = "ledstrip", IconType = "strip",
            LedsOn = isOn, Brightness = brightness, Hue = hue, Saturation = saturation,
            LedCount = effectiveLedCount,
            CanvasX = layout?.X ?? defX, CanvasY = layout?.Y ?? defY,
            CanvasW = layout?.W ?? defW, CanvasH = layout?.H ?? defH,
            CanvasRotation = ((((layout?.Rotation ?? 0) % 360) + 360) % 360),
            ParentDeviceId = parentDeviceId, ZoneIndex = zoneIndex,
            ZoneType = "linear", ZoneResizable = true,
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
            next.AddRange(current); next.Add(id);
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
        if (count < 0) return;
        _store.Update(s => s.Devices.ZoneLedCounts[id] = count);
    }

    public void Identify(string id, int durationMs) => _identify.Schedule(id, durationMs);

    // Reuse the DeviceFrame instance across RgbBridge's refresh so the writer
    // doesn't see a fresh zero-filled frame for one tick and blank the hub
    // (same rationale as MiniHub / NP50 providers).
    private readonly Dictionary<string, DeviceFrame> _frameCache = new();

    public IReadOnlyList<DeviceFrame> BuildFrames(int startingIndex)
    {
        if (!_hub.IsConnected) return Array.Empty<DeviceFrame>();
        var frames = new List<DeviceFrame>();
        var hubId = _hub.DeviceId;
        var idx = startingIndex;
        var settings = _store.Load();
        var layouts = settings.Lighting.DeviceLayouts;
        var counts = settings.Devices.ZoneLedCounts;
        var slot = 0;

        if (ReadMirror(settings, hubId))
        {
            frames.Add(BuildOrReuseFrame(MirrorId(hubId), MirrorLedCount(_hub, counts), 0, layouts, counts, ref idx));
        }
        else
        {
            foreach (var port in _hub.State.Ports)
            {
                frames.Add(BuildOrReuseFrame($"{hubId}:port{port.Channel}", port.LedCount, slot++, layouts, counts, ref idx));
            }
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
        string id, int firmwareLedCount, int zoneIndex,
        IReadOnlyDictionary<string, DeviceLayout> layouts,
        IReadOnlyDictionary<string, int> counts,
        ref int idx)
    {
        var effectiveLedCount = firmwareLedCount;
        if (counts.TryGetValue(id, out var persisted))
            effectiveLedCount = Math.Max(0, persisted);
        var (defX, defY, defW, defH) = DefaultSmartHubLayout(zoneIndex);
        layouts.TryGetValue(id, out var layout);
        var rot = ((((layout?.Rotation ?? 0) % 360) + 360) % 360);
        var thisIdx = idx++;

        if (_frameCache.TryGetValue(id, out var existing)
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
            index: thisIdx, id: id, ledCount: effectiveLedCount,
            x: layout?.X ?? defX, y: layout?.Y ?? defY,
            w: layout?.W ?? defW, h: layout?.H ?? defH, rotation: rot);
        _frameCache[id] = frame;
        return frame;
    }

    /// <summary>Default canvas slots for the four Smart Hub ARGB ports - a single row of four cards, same shape as the MiniHub default layout.</summary>
    internal static (float x, float y, float w, float h) DefaultSmartHubLayout(int slot)
    {
        const float Y = 463f;
        const float W = 220f;
        const float H = 60f;
        const float Gap = 240f;
        const float BaseX = 40f;
        const int Cols = 4;
        var s = ((slot % Cols) + Cols) % Cols;
        return (BaseX + s * Gap, Y, W, H);
    }
}
