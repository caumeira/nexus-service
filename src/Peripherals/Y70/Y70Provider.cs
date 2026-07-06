using System;
using Nexus.Service.Peripherals.Hyte.Y70Display;
using Nexus.Service.Persistence;
using Nexus.Service.Platform.Displays;

namespace Nexus.Service.Peripherals.Y70;

/// <summary>
/// Real <see cref="IY70Provider"/>: drives Y70 brightness + screen power on the
/// panel. Two transports, picked per model:
///   • Serial (Touch / Infinite): the STM32 controller's <c>FF CC 01</c> frame
///     over the CDC COM port managed by <see cref="Y70DisplayHub"/>.
///   • DDC/CI (Truly / GW): VCP 0x10 (brightness) + 0xD6 (power) via the
///     platform display-brightness provider.
/// Orientation goes through <see cref="IDisplayOrientationProvider"/> (Windows
/// rotation).
///
/// The config store is the source of truth the UI reads; every setter writes
/// the hardware first (best-effort) and then persists, so GET reflects the last
/// requested value even when the panel is briefly detached.
///
/// UNVERIFIED: the DDC/CI branch (Truly / GW) has NOT been tested on hardware;
/// only the serial path (Infinite, RTK0004) was on the bench. The VCP
/// codes/power values come from the legacy HYTE controllers; confirm brightness
/// (0x10) and power (0xD6: 0x01 on / 0x05 off) on a Truly or GW panel before
/// trusting this path.
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
    }

    public bool GetForceOrientation() => _store.Load().Y70.ForceOrientation;

    public void SetForceOrientation(bool forceOrientation)
    {
        _store.Update(s => s.Y70.ForceOrientation = forceOrientation);
    }

    /// <summary>Pushes the current effective orientation (PortraitFlipped when
    /// ForceOrientation is set, else the stored preference) to hardware.</summary>
    public void ApplyEffectiveOrientation()
    {
        var y70 = _store.Load().Y70;
        var effective = y70.ForceOrientation ? "PortraitFlipped" : y70.Orientation;
        var (ok, err) = _orientation.SetY70Orientation(effective);
        if (!ok) Console.Error.WriteLine($"[y70] rotate to '{effective}' failed: {err}");
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
    /// (the bench-verified path); falls back to DDC/CI for the DDC-only models.
    /// A failure here is logged but never throws.
    /// </summary>
    private void ApplyToHardware(bool screenOn, int pct)
    {
        // Firmware acts on the percentage (backlight) byte: screen-off must
        // send 0; a nonzero percentage keeps the panel lit even with the off
        // flag set. Screen-on floors to the firmware's minimum. Matches legacy
        // Y70TouchInfiniteController: off => SetCurrentBrightness(false, 0).
        var effective = screenOn ? Math.Max(pct, Y70DisplayProtocol.MinBrightnessOnPercent) : 0;

        if (_hub.IsConnected)
        {
            _hub.SetBrightnessPower(screenOn, effective);
            return;
        }

        // DDC/CI path (Truly / GW). UNVERIFIED - see class remarks.
        var id = DdcDisplayId();
        if (id is null) return; // no panel reachable; store-only
        _ddc.SetVcp(id, Y70DisplayProtocol.VcpPower, screenOn ? Y70DisplayProtocol.VcpPowerOn : Y70DisplayProtocol.VcpPowerOff);
        if (screenOn) _ddc.SetVcp(id, Y70DisplayProtocol.VcpBrightness, effective);
    }

    private string? DdcDisplayId() => _ddc.FindDisplayIdByHardwareName(Y70DisplayProtocol.DdcPanelHardwareNames);
}
