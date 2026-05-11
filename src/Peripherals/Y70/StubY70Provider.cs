using System;
using Qos.Service.Persistence;

namespace Qos.Service.Peripherals.Y70;

public sealed class StubY70Provider : IY70Provider
{
    private readonly IConfigStore _store;

    public StubY70Provider(IConfigStore store) { _store = store; }

    public bool IsConnected() => false;
    public string GetOrientation() => _store.Load().Y70.Orientation;
    public void SetOrientation(string orientation) =>
        _store.Update(s => s.Y70.Orientation = orientation);

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
