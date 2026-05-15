using System;
using Qos.Service.Persistence;
using Qos.Service.Platform.Displays;

namespace Qos.Service.Peripherals.Y70;

public sealed class StubY70Provider : IY70Provider
{
    private readonly IConfigStore _store;
    private readonly IDisplayOrientationProvider _orientation;

    public StubY70Provider(IConfigStore store, IDisplayOrientationProvider orientation)
    {
        _store = store;
        _orientation = orientation;
    }

    public bool IsConnected() => false;
    public string GetOrientation() => _store.Load().Y70.Orientation;
    public void SetOrientation(string orientation)
    {
        _store.Update(s => s.Y70.Orientation = orientation);
        // Best-effort: drive the actual Windows display rotation. The proxy
        // routes to the user-session helper, which calls ChangeDisplaySettingsEx
        // on the Y70 monitor. We don't fail the persist if rotation fails -
        // the Y70 may simply not be attached.
        var (ok, err) = _orientation.SetY70Orientation(orientation);
        if (!ok)
        {
            Console.Error.WriteLine($"[y70] rotate to '{orientation}' failed: {err}");
        }
    }

    public int GetBrightness() => _store.Load().Y70.Brightness;
    public void SetBrightness(int brightness) =>
        _store.Update(s => s.Y70.Brightness = Math.Clamp(brightness, 0, 255));

    public bool GetToggle() => _store.Load().Y70.ScreenOff;
    public void SetToggle(bool toggle) =>
        _store.Update(s => s.Y70.ScreenOff = toggle);

    public bool IsRotated()
    {
        var o = GetOrientation();
        return o == "Portrait" || o == "PortraitFlipped";
    }
}
