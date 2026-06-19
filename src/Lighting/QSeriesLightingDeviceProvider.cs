using System;
using System.Collections.Generic;
using Nexus.Service.Devices;            // ILightingDeviceProvider
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.Hyte.QSeriesCooler;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Surfaces the HYTE Q-series cooler (Q60 / Q80) as a single standalone
/// <see cref="LightingDevice"/> card, mirroring <see cref="CnvsLightingDeviceProvider"/>.
///
/// OpenRGB doesn't drive 1st-party HYTE devices, so <see cref="QSeriesCoolerHub"/>
/// owns the cooler's serial port and this provider exposes its LEDs to the
/// engine → <see cref="QSeriesLightingFrameWriter"/> pipeline. Renders one
/// linear zone of <see cref="QSeriesCoolerHub.LedCount"/> LEDs (the pump-head
/// channel); see the hub's WriteLighting for the multi-port streaming + the
/// on-device LED-topology caveat.
/// </summary>
public sealed class QSeriesLightingDeviceProvider : ILightingDeviceProvider, ILightingFrameContributor
{
    private readonly QSeriesCoolerHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private string _lastSignature = "";

    public QSeriesLightingDeviceProvider(QSeriesCoolerHub hub, IConfigStore store, Np50IdentifyTracker identify)
    {
        _hub = hub;
        _store = store;
        _identify = identify;
    }

    public bool IsConnected => _hub.IsConnected;

    /// <summary>
    /// The id OpenRGB assigns to the same cooler when it enumerates the COM port we hold
    /// (RgbDevice.StableId = "openrgb-l-{location}", and location is the COM port for these
    /// serial devices, e.g. "openrgb-l-COM4"). The composite strips this inert OpenRGB zombie
    /// by id - unaffected by OpenRGB's device name ("HYTE THICC Q60"), which silently broke a
    /// substring match. Null when disconnected. COM port names are alphanumeric, so no
    /// RgbDevice.Sanitize transform is needed to reconstruct the id.
    /// </summary>
    public string? OwnedOpenRgbDeviceId =>
        _hub.IsConnected && !string.IsNullOrEmpty(_hub.PortName) ? $"openrgb-l-{_hub.PortName}" : null;

    public event Action? DevicesChanged;

    /// <summary>
    /// Called by the Q-series heartbeat worker after a successful (re)open so the
    /// RgbBridge rebuilds its frame map. Cheap signature compare suppresses repeat
    /// invocations when nothing changed.
    /// </summary>
    public void OnHubStateUpdated()
    {
        var sig = _hub.IsConnected ? _hub.DeviceId : "disconnected";
        if (sig == _lastSignature) return;
        _lastSignature = sig;
        try { DevicesChanged?.Invoke(); } catch { /* swallow subscriber failures */ }
    }

    private static string CardName(string variant) => variant switch
    {
        QSeriesCoolerProtocol.VariantQ80 => "HYTE Q80",
        _ => "HYTE Q60",
    };

    public GetLightingDevicesResponse GetAll()
    {
        var resp = new GetLightingDevicesResponse { IsInit = true };
        if (!_hub.IsConnected) return resp;
        var id = _hub.DeviceId;
        var settings = _store.Load();
        resp.Devices.Add(BuildCard(
            id: id, name: CardName(_hub.Variant),
            deviceKey: Nexus.Service.Lighting.Mappings.DeviceKeyComputer.ForFirstParty(
                Peripherals.Hyte.QSeriesCooler.QSeriesCoolerProtocol.VendorId,
                Peripherals.Hyte.QSeriesCooler.QSeriesCoolerProtocol.ProductIdForVariant(_hub.Variant)),
            firmwareLedCount: QSeriesCoolerHub.LedCount,
            settings.Devices.DisabledLightingDevices,
            settings.Devices.LightingDevicePrefs,
            settings.Lighting.DeviceLayouts));
        return resp;
    }

    private static LightingDevice BuildCard(
        string id, string name, string deviceKey, int firmwareLedCount,
        IReadOnlyList<string> disabled,
        IReadOnlyDictionary<string, LightingDevicePreference> prefs,
        IReadOnlyDictionary<string, DeviceLayout> layouts)
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
        var (defX, defY, defW, defH) = DefaultQSeriesLayout();
        layouts.TryGetValue(id, out var layout);
        return new LightingDevice
        {
            Id = id, DeviceKey = deviceKey, Name = name, Type = "ledstrip", IconType = "cooler",
            LedsOn = isOn, Brightness = brightness, Hue = hue, Saturation = saturation,
            LedCount = firmwareLedCount,
            CanvasX = layout?.X ?? defX, CanvasY = layout?.Y ?? defY,
            CanvasW = layout?.W ?? defW, CanvasH = layout?.H ?? defH,
            CanvasRotation = ((((layout?.Rotation ?? 0) % 360) + 360) % 360),
            // Standalone top-level card (no ParentDeviceId / ZoneIndex). LED count is
            // fixed at the hub's per-port stream size; ZoneResizable=false hides the
            // led-count editor until on-device topology is confirmed.
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
        // No-op: Q-series LED count is fixed at the hub's per-port stream size;
        // ZoneResizable=false hides the editor in the UI.
        _ = id; _ = count;
    }

    public void Identify(string id, int durationMs) => _identify.Schedule(id, durationMs);

    // Hold the DeviceFrame across RgbBridge rebuilds so the per-LED buffer isn't
    // re-zeroed for one tick (same reuse pattern as CNVS / NP50 / MiniHub).
    private DeviceFrame? _frame;

    public IReadOnlyList<DeviceFrame> BuildFrames(int startingIndex)
    {
        if (!_hub.IsConnected) { _frame = null; return Array.Empty<DeviceFrame>(); }
        var id = _hub.DeviceId;
        var settings = _store.Load();
        var layouts = settings.Lighting.DeviceLayouts;
        var ledCount = QSeriesCoolerHub.LedCount;

        var (defX, defY, defW, defH) = DefaultQSeriesLayout();
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

    /// <summary>Default canvas placement for the Q-series cooler card. User can drag and persist;
    /// the composite re-grids anything without a saved layout anyway.</summary>
    private static (float x, float y, float w, float h) DefaultQSeriesLayout()
    {
        return (40f, 730f, 200f, 200f);
    }
}
