using System;
using System.Collections.Generic;
using Nexus.Service.Devices;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.Hyte.Cnvs;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Surfaces the HYTE CNVS as a single standalone <see cref="LightingDevice"/>
/// card on the lighting page — NOT a parent-with-child layout like
/// <see cref="Np50LightingDeviceProvider"/> uses for the multi-zone hubs.
/// CNVS is one physical mat with one 50-LED strip, so it renders as a
/// plain "HYTE CNVS" card alongside motherboard ARGB strips and GPU,
/// no parentDeviceId and no zone-child indirection.
///
/// Lives outside the OpenRGB stack because the service owns COM7 exclusively
/// via <see cref="CnvsHub"/> (see CnvsConnectionWorker for the grab-at-startup
/// race), so OpenRGB can't drive CNVS. This provider exposes CNVS to the
/// engine→writer pipeline.
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
        // CNVS is a single physical mat with one 50-LED strip — render as
        // a standalone card "HYTE CNVS", NOT a parent header with a child
        // zone (which is the NP50 / MiniHub pattern because those have
        // multiple distinct LED channels). Id matches DeviceId verbatim —
        // no ":mat" suffix — since there's only one zone to address.
        var id = _hub.DeviceId;
        var settings = _store.Load();
        resp.Devices.Add(BuildCard(
            id: id, name: "HYTE CNVS",
            firmwareLedCount: CnvsHub.LedCount,
            settings.Devices.DisabledLightingDevices,
            settings.Devices.LightingDevicePrefs,
            settings.Lighting.DeviceLayouts,
            settings.Devices.ZoneLedCounts));
        return resp;
    }

    private static LightingDevice BuildCard(
        string id, string name, int firmwareLedCount,
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
        // LED count is fixed at the firmware's 50; the card carries that
        // exact number (no user override). ZoneResizable=false on the
        // wire so the lighting page hides the led-count editor for CNVS.
        var (defX, defY, defW, defH) = DefaultCnvsLayout();
        layouts.TryGetValue(id, out var layout);
        return new LightingDevice
        {
            Id = id, Name = name, Type = "ledstrip", IconType = "mousemat",
            LedsOn = isOn, Brightness = brightness, Hue = hue, Saturation = saturation,
            LedCount = firmwareLedCount,
            CanvasX = layout?.X ?? defX, CanvasY = layout?.Y ?? defY,
            CanvasW = layout?.W ?? defW, CanvasH = layout?.H ?? defH,
            CanvasRotation = ((((layout?.Rotation ?? 0) % 360) + 360) % 360),
            // No ParentDeviceId / ZoneIndex / counts override — CNVS is a
            // top-level standalone card, not a child of a multi-zone hub.
            ZoneType = "linear", ZoneResizable = false,
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
        // No-op: CNVS LED count is fixed at the firmware-reported 50;
        // ZoneResizable=false hides the editor in the UI, but ignore any
        // stale calls defensively.
        _ = id; _ = count;
    }

    public void Identify(string id, int durationMs) => _identify.Schedule(id, durationMs);

    // Same frame-reuse pattern as NP50/MiniHub: hold the DeviceFrame across
    // RgbBridge.RefreshDevicesAsync rebuilds so the per-LED buffer doesn't
    // get re-zeroed for one tick and blank the mat.
    private DeviceFrame? _frame;

    public IReadOnlyList<DeviceFrame> BuildFrames(int startingIndex)
    {
        if (!_hub.IsConnected) { _frame = null; return Array.Empty<DeviceFrame>(); }
        var id = _hub.DeviceId;
        var settings = _store.Load();
        var layouts = settings.Lighting.DeviceLayouts;
        var ledCount = CnvsHub.LedCount;

        var (defX, defY, defW, defH) = DefaultCnvsLayout();
        layouts.TryGetValue(id, out var layout);
        var rot = ((((layout?.Rotation ?? 0) % 360) + 360) % 360);

        if (_frame is not null
            && _frame.Index == startingIndex
            && _frame.Id == id
            && _frame.LedCount == ledCount)
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
                index: startingIndex, id: id, ledCount: ledCount,
                x: layout?.X ?? defX, y: layout?.Y ?? defY,
                w: layout?.W ?? defW, h: layout?.H ?? defH, rotation: rot);
        }
        return new[] { _frame };
    }

    /// <summary>Default canvas placement for the CNVS mat. Placed below the
    /// MiniHub row so the three HYTE-device families coexist on a typical
    /// canvas. User can drag and persist.</summary>
    private static (float x, float y, float w, float h) DefaultCnvsLayout()
    {
        return (40f, 640f, 360f, 70f);
    }
}
