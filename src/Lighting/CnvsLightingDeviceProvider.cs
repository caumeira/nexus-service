using System;
using System.Collections.Generic;
using Nexus.Service.Devices;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.Hyte.Cnvs;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Surfaces the HYTE CNVS as a single drivable <see cref="LightingDevice"/>
/// (one mat = one 50-LED linear zone) on the lighting page, and contributes
/// a matching <see cref="DeviceFrame"/> to the engine each rebuild. Mirrors
/// <see cref="MiniHubLightingDeviceProvider"/> for the single-zone case;
/// no per-port split because CNVS only exposes one channel.
///
/// Why this lives outside the OpenRGB stack: our service now owns COM7
/// exclusively via <see cref="CnvsHub"/> (see CnvsConnectionWorker for
/// the grab-at-startup race), so OpenRGB can no longer drive CNVS. This
/// provider is what makes the engine→writer pipeline see CNVS again.
/// </summary>
public sealed class CnvsLightingDeviceProvider : ILightingDeviceProvider, ILightingFrameContributor
{
    private readonly CnvsHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private string _lastSignature = "";

    public CnvsLightingDeviceProvider(CnvsHub hub, IConfigStore store, Np50IdentifyTracker identify)
    {
        _hub = hub;
        _store = store;
        _identify = identify;
    }

    public bool IsConnected => _hub.IsConnected;

    public event Action? DevicesChanged;

    /// <summary>
    /// Called by the CNVS connection worker after a successful (re)open
    /// so the RgbBridge rebuilds its frame map. Cheap signature compare
    /// suppresses repeated invocations when the device hasn't changed.
    /// </summary>
    public void OnHubStateUpdated()
    {
        var sig = _hub.IsConnected ? _hub.DeviceId : "disconnected";
        if (sig == _lastSignature) return;
        _lastSignature = sig;
        try { DevicesChanged?.Invoke(); } catch { /* swallow subscriber failures */ }
    }

    public GetLightingDevicesResponse GetAll()
    {
        var resp = new GetLightingDevicesResponse { IsInit = true };
        if (!_hub.IsConnected) return resp;
        var id = $"{_hub.DeviceId}:mat";
        var settings = _store.Load();
        resp.Devices.Add(BuildZone(
            id: id, name: $"HYTE CNVS - Mat",
            firmwareLedCount: CnvsHub.LedCount,
            zoneIndex: 0, parentDeviceId: _hub.DeviceId,
            settings.Devices.DisabledLightingDevices,
            settings.Devices.LightingDevicePrefs,
            settings.Lighting.DeviceLayouts,
            settings.Devices.ZoneLedCounts));
        return resp;
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
        // LED count override: clamp to the firmware's fixed 50. CNVS LEDs
        // are non-resizable in hardware but we still let the user shrink
        // the logical strip (handy if part of the mat is occluded).
        var effectiveLedCount = firmwareLedCount;
        if (counts.TryGetValue(id, out var persisted))
            effectiveLedCount = Math.Clamp(persisted, 0, firmwareLedCount);
        var (defX, defY, defW, defH) = DefaultCnvsLayout(zoneIndex);
        layouts.TryGetValue(id, out var layout);
        return new LightingDevice
        {
            Id = id, Name = name, Type = "ledstrip", IconType = "mousemat",
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
        _store.Update(s => s.Devices.ZoneLedCounts[id] = Math.Clamp(count, 0, CnvsHub.LedCount));
    }

    public void Identify(string id, int durationMs) => _identify.Schedule(id, durationMs);

    // Same frame-reuse pattern as NP50/MiniHub: hold the DeviceFrame across
    // RgbBridge.RefreshDevicesAsync rebuilds so the per-LED buffer doesn't
    // get re-zeroed for one tick and blank the mat.
    private DeviceFrame? _frame;

    public IReadOnlyList<DeviceFrame> BuildFrames(int startingIndex)
    {
        if (!_hub.IsConnected) { _frame = null; return Array.Empty<DeviceFrame>(); }
        var id = $"{_hub.DeviceId}:mat";
        var settings = _store.Load();
        var layouts = settings.Lighting.DeviceLayouts;
        var counts = settings.Devices.ZoneLedCounts;

        var effectiveLedCount = CnvsHub.LedCount;
        if (counts.TryGetValue(id, out var persisted))
            effectiveLedCount = Math.Clamp(persisted, 0, CnvsHub.LedCount);

        var (defX, defY, defW, defH) = DefaultCnvsLayout(0);
        layouts.TryGetValue(id, out var layout);
        var rot = ((((layout?.Rotation ?? 0) % 360) + 360) % 360);

        if (_frame is not null
            && _frame.Index == startingIndex
            && _frame.Id == id
            && _frame.LedCount == effectiveLedCount)
        {
            _frame.X = layout?.X ?? defX;
            _frame.Y = layout?.Y ?? defY;
            _frame.W = layout?.W ?? defW;
            _frame.H = layout?.H ?? defH;
            _frame.Rotation = rot;
        }
        else
        {
            _frame = new DeviceFrame(
                index: startingIndex, id: id, ledCount: effectiveLedCount,
                x: layout?.X ?? defX, y: layout?.Y ?? defY,
                w: layout?.W ?? defW, h: layout?.H ?? defH, rotation: rot);
        }
        return new[] { _frame };
    }

    /// <summary>Default canvas placement for the CNVS mat. Placed below the
    /// MiniHub row so the three HYTE-device families coexist on a typical
    /// canvas. User can drag and persist.</summary>
    private static (float x, float y, float w, float h) DefaultCnvsLayout(int _slot)
    {
        return (40f, 640f, 360f, 70f);
    }
}
