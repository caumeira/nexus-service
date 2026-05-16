using System.Collections.Generic;
using Qos.Service.Devices;
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
public sealed class Np50LightingDeviceProvider : ILightingDeviceProvider
{
    private readonly Np50Hub _hub;

    public Np50LightingDeviceProvider(Np50Hub hub)
    {
        _hub = hub;
    }

    public bool IsConnected => _hub.IsConnected;

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

    // Setters are no-ops in v1. The composite provider routes by id prefix
    // so OpenRGB calls don't accidentally land here.
    public void SetDisabled(IReadOnlyList<string> ids) { }
    public void SetPower(string id, bool on) { }
    public void SetBrightness(string id, int brightness) { }
    public void SetHue(string id, float hue) { }
    public void SetSaturation(string id, float saturation) { }
    public void SetZoneLedCount(string id, int count) { }
    public void Identify(string id, int durationMs) { }
}
