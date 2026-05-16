using System;
using System.Collections.Generic;
using Qos.Service.Devices;
using Qos.Service.Lighting.Engine;
using Qos.Service.Models.Devices;
using Qos.Service.Peripherals.Hyte.Np50;

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

    public Np50LightingDeviceProvider(Np50Hub hub)
    {
        _hub = hub;
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

        // Parent device: the NP50 hub itself. No own LEDs (the firmware
        // logo strip is part of port-1 streaming, not a separate zone).
        resp.Devices.Add(new LightingDevice
        {
            Id = hubId,
            Name = "HYTE NP50",
            Type = "Hub",
            IconType = "hub",
            LedsOn = true,
            Brightness = 100,
            LedCount = 0,
        });

        // Children: every attached module, in port + chain order. We index
        // ZoneIndex globally across the hub so the frontend can use a
        // single integer to identify which slot a click came from.
        var zoneIdx = 0;
        foreach (var port in _hub.State.Ports)
        {
            foreach (var dev in port.Devices)
            {
                resp.Devices.Add(new LightingDevice
                {
                    Id = $"{hubId}:port{port.Index}:dev{dev.Index}",
                    Name = $"{dev.Model} (Port {port.Index} #{dev.Index})",
                    Type = dev.Model, // "LS10" | "LS30" | "FP12"
                    IconType = dev.Model == "FP12" ? "fan" : "strip",
                    LedsOn = true,
                    Brightness = 100,
                    LedCount = dev.LedCount,
                    ParentDeviceId = hubId,
                    ZoneIndex = zoneIdx++,
                });
            }
        }
        return resp;
    }

    // Setters are no-ops at the provider level — the LightingEngine + RgbBridge
    // pipeline reads the same DisabledLightingDevices / LightingDevicePrefs
    // settings that drive OpenRGB devices, so power / brightness / disabled
    // already work for NP50 frames via the shared frame-write path. Hue /
    // saturation aren't device-side knobs; they map to whatever effect the
    // engine is currently rendering. SetZoneLedCount is a no-op because NP50
    // module LED counts are fixed by the firmware (LS10=20, LS30=62).
    public void SetDisabled(IReadOnlyList<string> ids) { }
    public void SetPower(string id, bool on) { }
    public void SetBrightness(string id, int brightness) { }
    public void SetHue(string id, float hue) { }
    public void SetSaturation(string id, float saturation) { }
    public void SetZoneLedCount(string id, int count) { }
    public void Identify(string id, int durationMs) { }

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
