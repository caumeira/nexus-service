using System;
using System.Collections.Generic;
using Qos.Service.Devices;
using Qos.Service.Models.Devices;

namespace Qos.Service.Lighting;

/// <summary>
/// Aggregates the OpenRGB-backed lighting devices (motherboard, RAM, AIO,
/// etc.) with NP50 hub devices behind a single <see cref="ILightingDeviceProvider"/>
/// so the existing /devices/lighting-devices/* routes and the React lighting
/// page don't need to know there's a second source. Routes by id prefix:
/// anything starting with <c>np50:</c> goes to the NP50 provider; everything
/// else stays on the OpenRGB provider.
///
/// Mirrors <see cref="Cooling.CompositeFanControlProvider"/> in spirit.
/// </summary>
public sealed class CompositeLightingDeviceProvider : ILightingDeviceProvider
{
    private readonly ILightingDeviceProvider _openRgb;
    private readonly Np50LightingDeviceProvider _np50;

    public CompositeLightingDeviceProvider(ILightingDeviceProvider openRgb, Np50LightingDeviceProvider np50)
    {
        _openRgb = openRgb;
        _np50 = np50;
    }

    public bool IsConnected => _openRgb.IsConnected || _np50.IsConnected;

    public GetLightingDevicesResponse GetAll()
    {
        var rgb = _openRgb.GetAll();
        var hub = _np50.GetAll();

        // Filter out OpenRGB's NP50 entry when our own provider is live.
        // OpenRGB used to drive the NP50 logo strip via the device's COM
        // port, but qos-service now opens that port exclusively for the
        // hub heartbeat — OpenRGB's entry becomes a zombie that the
        // animation system can't actually push frames to. Hide it so the
        // user only sees the real, controllable NP50 surface.
        if (_np50.IsConnected && rgb.Devices.Count > 0)
        {
            rgb.Devices.RemoveAll(d =>
                d.Name.Contains("Nexus Portal NP50", StringComparison.OrdinalIgnoreCase) ||
                d.Name.Contains("HYTE NP50", StringComparison.OrdinalIgnoreCase));
        }

        if (hub.Devices.Count == 0) return rgb;
        // IsInit follows whichever side has actually initialized; the UI just
        // wants to know "is anything ready to render?". OR both flags.
        rgb.IsInit = rgb.IsInit || hub.IsInit;
        rgb.Devices.AddRange(hub.Devices);
        return rgb;
    }

    public void SetDisabled(IReadOnlyList<string> ids)
    {
        // Per-id routing: split the ids and dispatch each batch to its owner.
        // Keeps both providers' "I own these ids" invariants intact.
        var rgbIds = new List<string>(ids.Count);
        var hubIds = new List<string>(ids.Count);
        foreach (var id in ids)
            (IsNp50Id(id) ? hubIds : rgbIds).Add(id);
        if (rgbIds.Count > 0) _openRgb.SetDisabled(rgbIds);
        if (hubIds.Count > 0) _np50.SetDisabled(hubIds);
    }

    public void SetPower(string id, bool on) { Pick(id).SetPower(id, on); }
    public void SetBrightness(string id, int brightness) { Pick(id).SetBrightness(id, brightness); }
    public void SetHue(string id, float hue) { Pick(id).SetHue(id, hue); }
    public void SetSaturation(string id, float saturation) { Pick(id).SetSaturation(id, saturation); }
    public void SetZoneLedCount(string id, int count) { Pick(id).SetZoneLedCount(id, count); }
    public void Identify(string id, int durationMs) { Pick(id).Identify(id, durationMs); }

    private ILightingDeviceProvider Pick(string id) => IsNp50Id(id) ? _np50 : _openRgb;

    private static bool IsNp50Id(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith("np50:", StringComparison.Ordinal);
}
