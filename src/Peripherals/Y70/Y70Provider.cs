using System;
using Nexus.Service.Peripherals.Hyte.Y70Display;
using Nexus.Service.Persistence;
using Nexus.Service.Platform.Displays;

namespace Nexus.Service.Peripherals.Y70;

/// <summary>
/// Real <see cref="IY70Provider"/>: drives Y70 brightness + screen power on the
/// actual panel, replacing the persist-only stub. Two transports, picked per
/// model:
///   • Serial (Touch / Infinite): the STM32 controller's <c>FF CC 01</c> frame
///     over the CDC COM port already managed by <see cref="Y70DisplayHub"/>.
///   • DDC/CI (Truly / GW): VCP 0x10 (brightness) + 0xD6 (power) via the
///     platform display-brightness provider.
/// Orientation continues to go through <see cref="IDisplayOrientationProvider"/>
/// (real Windows rotation), unchanged from the previous stub.
///
/// The config store stays the source of truth the UI reads; every setter writes
/// the hardware first (best-effort) and then persists, so GET reflects the last
/// requested value even when the panel is briefly detached.
///
/// ⚠️ UNVERIFIED: the DDC/CI branch (Truly / GW) has NOT been tested on real
/// hardware — only the serial path (Infinite, RTK0004) was on the bench. The
/// VCP codes/power values come from the legacy HYTE controllers; confirm
/// brightness (0x10) and power (0xD6: 0x01 on / 0x05 off) on a Truly or GW
/// panel before trusting this path.
/// </summary>
public sealed class Y70Provider : IY70Provider
{
    private readonly Y70DisplayHub _hub;
    private readonly IConfigStore _store;
    private readonly IDisplayOrientationProvider _orientation;
    private readonly IDisplayBrightnessProvider _ddc;

    public Y70Provider(
        Y70DisplayHub hub,
        IConfigStore store,
        IDisplayOrientationProvider orientation,
        IDisplayBrightnessProvider ddc)
    {
        _hub = hub;
        _store = store;
        _orientation = orientation;
        _ddc = ddc;
    }

    public bool IsConnected() => _hub.IsConnected || DdcDisplayId() is not null;

    public string GetOrientation() => _store.Load().Y70.Orientation;

    public void SetOrientation(string orientation)
    {
        _store.Update(s => s.Y70.Orientation = orientation);
        var (ok, err) = _orientation.SetY70Orientation(orientation);
        if (!ok) Console.Error.WriteLine($"[y70] rotate to '{orientation}' failed: {err}");
    }

    public int GetBrightness() => _store.Load().Y70.Brightness;

    public void SetBrightness(int brightness)
    {
        var pct = Math.Clamp(brightness, 0, 100);
        var screenOn = !_store.Load().Y70.ScreenOff;
        ApplyToHardware(screenOn, pct);
        _store.Update(s => s.Y70.Brightness = pct);
    }

    public bool GetToggle() => _store.Load().Y70.ScreenOff;

    public void SetToggle(bool screenOff)
    {
        ApplyToHardware(!screenOff, _store.Load().Y70.Brightness);
        _store.Update(s => s.Y70.ScreenOff = screenOff);
    }

    public bool IsRotated()
    {
        var o = GetOrientation();
        return o == "Portrait" || o == "PortraitFlipped";
    }

    /// <summary>
    /// Push the desired brightness/power to the panel. Serial controller first
    /// (the only bench-verified path); falls back to DDC/CI for the DDC-only
    /// models. A failure here is logged but never throws — the store write still
    /// happens so the UI stays consistent and a reconnect can re-apply.
    /// </summary>
    private void ApplyToHardware(bool screenOn, int pct)
    {
        // Firmware floors a screen-on brightness; mirror it so the panel and the
        // stored value don't drift below the clamp.
        var effective = screenOn ? Math.Max(pct, Y70DisplayProtocol.MinBrightnessOnPercent) : pct;

        if (_hub.IsConnected)
        {
            _hub.SetBrightnessPower(screenOn, effective);
            return;
        }

        // DDC/CI path (Truly / GW). UNVERIFIED — see class remarks.
        var id = DdcDisplayId();
        if (id is null) return; // no panel reachable; store-only, like a detached device
        _ddc.SetVcp(id, Y70DisplayProtocol.VcpPower, screenOn ? Y70DisplayProtocol.VcpPowerOn : Y70DisplayProtocol.VcpPowerOff);
        if (screenOn) _ddc.SetVcp(id, Y70DisplayProtocol.VcpBrightness, effective);
    }

    private string? DdcDisplayId() => _ddc.FindDisplayIdByHardwareName(Y70DisplayProtocol.DdcPanelHardwareNames);
}
