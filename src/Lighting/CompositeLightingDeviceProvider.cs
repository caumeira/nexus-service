using System;
using System.Collections.Generic;
using Nexus.Service.Devices;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Models.Devices;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Aggregates the OpenRGB-backed lighting devices (motherboard, RAM, AIO, etc.) with NP50 + MiniHub hub devices behind a
/// single <see cref="ILightingDeviceProvider"/> so the /devices/lighting-devices/* routes and the React lighting page don't
/// have to know there's more than one source. Routes by id prefix: anything starting with <c>np50:</c> or <c>minihub:</c>
/// goes to the matching hub provider; everything else stays on the OpenRGB provider. Also the layout authority — applies
/// <see cref="CanvasGridLayout"/> to every device without a persisted layout and mirrors the result into engine frames so
/// running effects sample from the same rectangles the SPA shows. Mirrors <see cref="Cooling.CompositeFanControlProvider"/>.
/// </summary>
public sealed class CompositeLightingDeviceProvider : ILightingDeviceProvider
{
    private readonly ILightingDeviceProvider _openRgb;
    private readonly Np50LightingDeviceProvider _np50;
    private readonly MiniHubLightingDeviceProvider _miniHub;
    private readonly IConfigStore _store;
    private readonly LightingEngine _engine;

    public CompositeLightingDeviceProvider(
        ILightingDeviceProvider openRgb,
        Np50LightingDeviceProvider np50,
        MiniHubLightingDeviceProvider miniHub,
        IConfigStore store,
        LightingEngine engine)
    {
        _openRgb = openRgb;
        _np50 = np50;
        _miniHub = miniHub;
        _store = store;
        _engine = engine;
    }

    public bool IsConnected => _openRgb.IsConnected || _np50.IsConnected || _miniHub.IsConnected;

    public GetLightingDevicesResponse GetAll()
    {
        var rgb = _openRgb.GetAll();

        // Filter out OpenRGB's NP50/MiniHub entries when our own providers
        // are live. nexus-service now opens those COM ports exclusively for
        // hub control; OpenRGB's entries become zombies the animation
        // system can't push frames to.
        if (rgb.Devices.Count > 0)
        {
            if (_np50.IsConnected)
            {
                rgb.Devices.RemoveAll(d =>
                    d.Name.Contains("Nexus Portal NP50", StringComparison.OrdinalIgnoreCase) ||
                    d.Name.Contains("HYTE NP50", StringComparison.OrdinalIgnoreCase));
            }
            if (_miniHub.IsConnected)
            {
                rgb.Devices.RemoveAll(d =>
                    d.Name.Contains("MiniHub", StringComparison.OrdinalIgnoreCase) ||
                    d.Name.Contains("HYTE Mini", StringComparison.OrdinalIgnoreCase));
            }
        }

        var hub = _np50.GetAll();
        if (hub.Devices.Count > 0)
        {
            rgb.IsInit = rgb.IsInit || hub.IsInit;
            rgb.Devices.AddRange(hub.Devices);
        }
        var mini = _miniHub.GetAll();
        if (mini.Devices.Count > 0)
        {
            rgb.IsInit = rgb.IsInit || mini.IsInit;
            rgb.Devices.AddRange(mini.Devices);
        }

        // Spread every device without a persisted layout across the grid. totalCount counts persisted devices too so the
        // slot for any one device is stable across calls (resetting one card doesn't shuffle the others).
        var settings = _store.Load();
        var layouts = settings.Lighting.DeviceLayouts;
        var total = rgb.Devices.Count;
        for (var i = 0; i < total; i++)
        {
            var dev = rgb.Devices[i];
            if (layouts.ContainsKey(dev.Id)) continue;
            var (x, y, w, h) = CanvasGridLayout.Slot(i, total);
            dev.CanvasX = x; dev.CanvasY = y; dev.CanvasW = w; dev.CanvasH = h;
        }

        // Mirror into engine frames so running effects sample from the same rectangles. Without this, RgbBridge's
        // existing-frame-wins rule would pin pre-grid coordinates in memory. O(N²), trivial for typical N (<30).
        foreach (var frame in _engine.Devices)
        {
            for (var i = 0; i < rgb.Devices.Count; i++)
            {
                var dev = rgb.Devices[i];
                if (dev.Id != frame.Id) continue;
                frame.X = dev.CanvasX; frame.Y = dev.CanvasY; frame.W = dev.CanvasW; frame.H = dev.CanvasH;
                break;
            }
        }
        return rgb;
    }

    public void SetDisabled(IReadOnlyList<string> ids)
    {
        // Per-id routing: split the ids and dispatch each batch to its owner.
        // Keeps each provider's "I own these ids" invariants intact.
        var rgbIds = new List<string>(ids.Count);
        var np50Ids = new List<string>(ids.Count);
        var miniIds = new List<string>(ids.Count);
        foreach (var id in ids)
        {
            if (IsNp50Id(id)) np50Ids.Add(id);
            else if (IsMiniHubId(id)) miniIds.Add(id);
            else rgbIds.Add(id);
        }
        if (rgbIds.Count > 0) _openRgb.SetDisabled(rgbIds);
        if (np50Ids.Count > 0) _np50.SetDisabled(np50Ids);
        if (miniIds.Count > 0) _miniHub.SetDisabled(miniIds);
    }

    public void SetPower(string id, bool on) { Pick(id).SetPower(id, on); }
    public void SetBrightness(string id, int brightness) { Pick(id).SetBrightness(id, brightness); }
    public void SetHue(string id, float hue) { Pick(id).SetHue(id, hue); }
    public void SetSaturation(string id, float saturation) { Pick(id).SetSaturation(id, saturation); }
    public void SetZoneLedCount(string id, int count) { Pick(id).SetZoneLedCount(id, count); }
    public void Identify(string id, int durationMs) { Pick(id).Identify(id, durationMs); }

    private ILightingDeviceProvider Pick(string id)
        => IsNp50Id(id) ? _np50 : IsMiniHubId(id) ? _miniHub : _openRgb;

    private static bool IsNp50Id(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith("np50:", StringComparison.Ordinal);

    private static bool IsMiniHubId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith("minihub:", StringComparison.Ordinal);
}
