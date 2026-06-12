using System;
using System.Collections.Generic;
using Nexus.Service.Devices;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.Hyte.Keeb;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Exposes the directly-driven HYTE Keeb TKL as two lighting zones — the key
/// matrix and the underglow — for the lighting page, and contributes their
/// per-frame <see cref="DeviceFrame"/>s to the engine so canvas effects,
/// brightness, and identify apply uniformly. Mirrors
/// <see cref="Np50LightingDeviceProvider"/>; the keeb owns its vendor HID
/// interface, so OpenRGB doesn't drive it.
/// </summary>
public sealed class KeebLightingDeviceProvider : ILightingDeviceProvider, ILightingFrameContributor
{
    /// <summary>Zone id suffixes (also the engine frame ids).</summary>
    public const string KeysSuffix = ":keys";
    public const string UnderglowSuffix = ":underglow";

    private readonly KeebHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private readonly Dictionary<string, DeviceFrame> _frameCache = new();

    public KeebLightingDeviceProvider(KeebHub hub, IConfigStore store, Np50IdentifyTracker identify)
    {
        _hub = hub;
        _store = store;
        _identify = identify;
    }

    public bool IsConnected => _hub.IsConnected;

    public event Action? DevicesChanged;

    /// <summary>Called by the connection worker when the keeb appears/disappears.</summary>
    public void OnConnectionChanged()
    {
        try { DevicesChanged?.Invoke(); } catch { /* subscriber failures shouldn't bubble */ }
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
        var zoneLedCounts = settings.Devices.ZoneLedCounts;

        resp.Devices.Add(BuildZone(
            id: hubId + KeysSuffix, name: $"{KeebHub.ProductName} - Keys",
            deviceKey: Nexus.Service.Lighting.Mappings.DeviceKeyComputer.ForFirstParty(
                Peripherals.Hyte.Keeb.KeebProtocol.VendorId, Peripherals.Hyte.Keeb.KeebProtocol.ProductId, "keys"),
            iconType: "keyboard", firmwareLedCount: KeebLayout.KeyLedCount,
            zoneIndex: 0, parentDeviceId: hubId, disabled, prefs, layouts, zoneLedCounts));
        resp.Devices.Add(BuildZone(
            id: hubId + UnderglowSuffix, name: $"{KeebHub.ProductName} - Underglow",
            deviceKey: Nexus.Service.Lighting.Mappings.DeviceKeyComputer.ForFirstParty(
                Peripherals.Hyte.Keeb.KeebProtocol.VendorId, Peripherals.Hyte.Keeb.KeebProtocol.ProductId, "underglow"),
            iconType: "strip", firmwareLedCount: KeebLayout.SurroundLedCount,
            zoneIndex: 1, parentDeviceId: hubId, disabled, prefs, layouts, zoneLedCounts));
        return resp;
    }

    private static LightingDevice BuildZone(
        string id, string name, string deviceKey, string iconType, int firmwareLedCount,
        int zoneIndex, string parentDeviceId,
        IReadOnlyList<string> disabled,
        IReadOnlyDictionary<string, LightingDevicePreference> prefs,
        IReadOnlyDictionary<string, Persistence.DeviceLayout> layouts,
        IReadOnlyDictionary<string, int> zoneLedCounts)
    {
        var isOn = true;
        for (var i = 0; i < disabled.Count; i++)
        { if (disabled[i] == id) { isOn = false; break; } }
        var brightness = 100;
        var hue = 0f;
        var saturation = 1f;
        if (prefs.TryGetValue(id, out var pref))
        {
            brightness = pref.Brightness; hue = pref.Hue; saturation = pref.Saturation;
        }
        var effectiveLedCount = firmwareLedCount;
        if (zoneLedCounts.TryGetValue(id, out var persisted))
            effectiveLedCount = Math.Clamp(persisted, 0, firmwareLedCount);

        var (defX, defY, defW, defH) = DefaultKeebLayout(zoneIndex);
        layouts.TryGetValue(id, out var layout);
        return new LightingDevice
        {
            Id = id,
            Name = name,
            Type = "ledstrip",
            IconType = iconType,
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
            ParentDeviceId = parentDeviceId,
            ZoneIndex = zoneIndex,
            ZoneType = "linear",
            ZoneResizable = false,
        };
    }

    // ── Persisted setters (same shared store OpenRGB / NP50 use) ──

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

    // Keeb zone LED counts are firmware-fixed; ignore shrink requests rather
    // than letting the canvas under-address the matrix.
    public void SetZoneLedCount(string id, int count) { /* fixed layout */ }

    public void Identify(string id, int durationMs) => _identify.Schedule(id, durationMs);

    // ── ILightingFrameContributor ──

    public IReadOnlyList<DeviceFrame> BuildFrames(int startingIndex)
    {
        if (!_hub.IsConnected) return Array.Empty<DeviceFrame>();
        var hubId = _hub.DeviceId;
        var settings = _store.Load();
        var layouts = settings.Lighting.DeviceLayouts;
        var idx = startingIndex;
        var frames = new List<DeviceFrame>(2)
        {
            BuildOrReuseFrame(hubId + KeysSuffix, KeebLayout.KeyLedCount, 0, layouts, ref idx),
            BuildOrReuseFrame(hubId + UnderglowSuffix, KeebLayout.SurroundLedCount, 1, layouts, ref idx),
        };
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
        string id, int ledCount, int zoneIndex,
        IReadOnlyDictionary<string, Persistence.DeviceLayout> layouts, ref int idx)
    {
        var (defX, defY, defW, defH) = DefaultKeebLayout(zoneIndex);
        layouts.TryGetValue(id, out var layout);
        var rot = ((((layout?.Rotation ?? 0) % 360) + 360) % 360);
        var thisIdx = idx++;
        if (_frameCache.TryGetValue(id, out var existing)
            && existing.Index == thisIdx && existing.LedCount == ledCount)
        {
            existing.X = layout?.X ?? defX;
            existing.Y = layout?.Y ?? defY;
            existing.W = layout?.W ?? defW;
            existing.H = layout?.H ?? defH;
            existing.Rotation = rot;
            return existing;
        }
        var frame = new DeviceFrame(thisIdx, id, ledCount,
            layout?.X ?? defX, layout?.Y ?? defY, layout?.W ?? defW, layout?.H ?? defH, rot);
        _frameCache[id] = frame;
        return frame;
    }

    /// <summary>
    /// Default canvas slots: a wide keys band and a thinner underglow band
    /// beneath it (canvas is 0..1000 × 0..600). User can drag afterward.
    /// </summary>
    internal static (float x, float y, float w, float h) DefaultKeebLayout(int zoneIndex) =>
        zoneIndex == 0 ? (120f, 150f, 760f, 140f) : (120f, 300f, 760f, 50f);
}
