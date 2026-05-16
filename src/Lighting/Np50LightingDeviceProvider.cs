using System;
using System.Collections.Generic;
using Qos.Service.Devices;
using Qos.Service.Lighting.Engine;
using Qos.Service.Models.Devices;
using Qos.Service.Peripherals.Hyte.Np50;
using Qos.Service.Persistence;

namespace Qos.Service.Lighting;

/// <summary>
/// Exposes the NP50 hub and its attached Nexus Link modules (LS10 / LS30 /
/// FP12) as <see cref="LightingDevice"/>s for the lighting page. The hub
/// itself surfaces as a parent device; each attached module surfaces as a
/// child with <see cref="LightingDevice.ParentDeviceId"/> pointing at the
/// hub, so the existing DevicePanel grouping logic (built for motherboard
/// zones) renders them as a collapsible group "for free".
///
/// v1 is read-only — the GetAll snapshot lets the UI render the tree, but
/// the per-zone Set* operations (power, brightness, hue, saturation) are
/// no-ops. Phase-3.5 will wire them into <see cref="Np50Hub.WriteLighting"/>
/// once the per-zone color picker shape settles.
/// </summary>
public sealed class Np50LightingDeviceProvider : ILightingDeviceProvider, ILightingFrameContributor
{
    /// <summary>Number of LEDs on the NP50 hub logo strip (the firmware-controlled prefix on streaming channel 1).</summary>
    public const int LogoLedCount = 6;

    /// <summary>
    /// Cached fan-list signature used to debounce <see cref="DevicesChanged"/>:
    /// we only fire when the actual lit-device topology changes (a strip is
    /// plugged/unplugged), not on every fan-temp/RPM tick.
    /// </summary>
    private string _lastSignature = "";

    private readonly Np50Hub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;

    public Np50LightingDeviceProvider(Np50Hub hub, IConfigStore store, Np50IdentifyTracker identify)
    {
        _hub = hub;
        _store = store;
        _identify = identify;
    }

    public bool IsConnected => _hub.IsConnected;

    public event Action? DevicesChanged;

    /// <summary>
    /// Called by the heartbeat worker (which polls the hub) so the bridge
    /// can rebuild frame mappings when the lit-device topology changes.
    /// Cheap signature compare keeps non-topology ticks silent.
    /// </summary>
    public void OnHubStateUpdated()
    {
        var sig = BuildSignature();
        if (sig == _lastSignature) return;
        _lastSignature = sig;
        try { DevicesChanged?.Invoke(); } catch { /* subscriber failures shouldn't bubble */ }
    }

    private string BuildSignature()
    {
        if (!_hub.IsConnected) return "disconnected";
        var sb = new System.Text.StringBuilder(_hub.DeviceId);
        sb.Append('|');
        foreach (var port in _hub.State.Ports)
        {
            sb.Append("p").Append(port.Index).Append(':');
            foreach (var dev in port.Devices)
            {
                sb.Append(dev.Model).Append(dev.LedCount).Append(',');
            }
            sb.Append(';');
        }
        return sb.ToString();
    }

    public GetLightingDevicesResponse GetAll()
    {
        var resp = new GetLightingDevicesResponse { IsInit = true };
        if (!_hub.IsConnected) return resp;

        var hubId = _hub.DeviceId;
        var settings = _store.Load();
        var disabled = settings.Devices.DisabledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;

        // Each NP50-driven zone is a standalone drivable device. We don't
        // emit the bare hub (no LEDs of its own → renders as "detected but
        // not drivable" clutter in the UI) and we don't use parentDeviceId
        // — that field is reserved for OpenRGB motherboard-zone splits the
        // UI knows how to render. Top-level cards with descriptive names
        // ("HYTE NP50 Logo", "LS10 (Port 1 #1)", …) keep the lighting page
        // surface uniform.

        resp.Devices.Add(BuildDeviceEntry(
            id: $"{hubId}:logo",
            name: "HYTE NP50 Logo",
            type: "ledstrip",
            iconType: "strip",
            ledCount: LogoLedCount,
            disabled, prefs));

        foreach (var port in _hub.State.Ports)
        {
            foreach (var dev in port.Devices)
            {
                if (dev.LedCount <= 0) continue;
                resp.Devices.Add(BuildDeviceEntry(
                    id: $"{hubId}:port{port.Index}:dev{dev.Index}",
                    name: $"{dev.Model} (NP50 Port {port.Index} #{dev.Index})",
                    type: "ledstrip",
                    iconType: dev.Model == "FP12" ? "fan" : "strip",
                    ledCount: dev.LedCount,
                    disabled, prefs));
            }
        }
        return resp;
    }

    private static LightingDevice BuildDeviceEntry(
        string id, string name, string type, string iconType, int ledCount,
        IReadOnlyList<string> disabled,
        IReadOnlyDictionary<string, LightingDevicePreference> prefs)
    {
        // Honour persisted state when reporting the card so the UI's toggle
        // and brightness slider reflect what the writer is actually doing.
        var isOn = true;
        for (var i = 0; i < disabled.Count; i++)
        { if (disabled[i] == id) { isOn = false; break; } }
        var brightness = 100;
        var hue = 0f;
        var saturation = 0f;
        if (prefs.TryGetValue(id, out var pref))
        {
            brightness = pref.Brightness;
            hue = pref.Hue;
            saturation = pref.Saturation;
        }
        return new LightingDevice
        {
            Id = id,
            Name = name,
            Type = type,
            IconType = iconType,
            LedsOn = isOn,
            Brightness = brightness,
            Hue = hue,
            Saturation = saturation,
            LedCount = ledCount,
        };
    }

    // Setters persist to the same shared settings store OpenRGB uses, so the
    // engine→writer pipeline picks up the new state on the next frame.
    // Mirrors OpenRgbLightingDeviceProvider's atomic list-replacement
    // strategy so the 30fps frame reader never sees a torn DisabledList.

    public void SetDisabled(IReadOnlyList<string> ids) => _store.Update(s =>
    {
        s.Devices.DisabledLightingDevices = new List<string>(ids);
    });

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
        {
            pref = new LightingDevicePreference();
            s.Devices.LightingDevicePrefs[id] = pref;
        }
        pref.Brightness = Math.Clamp(brightness, 0, 100);
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

    // NP50 module LED counts are fixed by the firmware (LS10=20, LS30=62);
    // there's no RESIZEZONE equivalent in the protocol.
    public void SetZoneLedCount(string id, int count) { }

    public void Identify(string id, int durationMs) => _identify.Schedule(id, durationMs);

    // ── ILightingFrameContributor ──

    public IReadOnlyList<DeviceFrame> BuildFrames(int startingIndex)
    {
        if (!_hub.IsConnected) return Array.Empty<DeviceFrame>();
        var frames = new List<DeviceFrame>();
        var hubId = _hub.DeviceId;
        var idx = startingIndex;

        // Logo strip first — 6 LEDs on the hub itself, prefixed onto port-1
        // streaming. Layout: a small rectangle to the right of the cards.
        // Engine canvas-samples linearly along this rect since there's no
        // matrix UV (LedU/LedV) provided.
        frames.Add(new DeviceFrame(
            index: idx++,
            id: $"{hubId}:logo",
            ledCount: LogoLedCount,
            x: 820, y: 480, w: 80, h: 30, rotation: 0));

        // Each Nexus Link module is its own zone. Lay them out vertically,
        // grouped per port, so the canvas sample produces a sensible default
        // even before the user repositions them on the lighting page.
        const float baseY = 320f;
        const float perRowY = 50f;
        foreach (var port in _hub.State.Ports)
        {
            for (var i = 0; i < port.Devices.Count; i++)
            {
                var dev = port.Devices[i];
                if (dev.LedCount <= 0) continue;
                frames.Add(new DeviceFrame(
                    index: idx++,
                    id: $"{hubId}:port{port.Index}:dev{dev.Index}",
                    ledCount: dev.LedCount,
                    x: 820,
                    y: baseY + (port.Index - 1) * 110f + i * perRowY,
                    w: 160, h: 30, rotation: 0));
            }
        }
        return frames;
    }
}

/// <summary>
/// Singleton tracker for identify-flash requests against NP50-driven LEDs.
/// Lets <see cref="Np50LightingDeviceProvider.Identify"/> schedule a flash
/// and <see cref="Np50LightingFrameWriter"/> read it without a direct
/// dependency between the two (which would otherwise form a cycle through
/// DI on the lighting engine path).
/// </summary>
public sealed class Np50IdentifyTracker
{
    private readonly object _lock = new();
    private readonly Dictionary<string, (long startTicks, long expirationTicks)> _entries = new();

    public void Schedule(string id, int durationMs)
    {
        if (string.IsNullOrEmpty(id)) return;
        var now = DateTime.UtcNow.Ticks;
        var dur = Math.Max(1, durationMs);
        lock (_lock) _entries[id] = (now, now + TimeSpan.FromMilliseconds(dur).Ticks);
    }

    public bool TryGetActive(string id, long nowTicks, out long startTicks)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(id, out var entry) && nowTicks < entry.expirationTicks)
            {
                startTicks = entry.startTicks;
                return true;
            }
            if (entry.expirationTicks != 0) _entries.Remove(id);
        }
        startTicks = 0;
        return false;
    }
}
