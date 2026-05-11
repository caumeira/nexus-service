using Qos.Service.Devices;

namespace Qos.Service.Tests;

public class LightingDevicePowerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;
    private readonly TestableConfigStore _store;

    public LightingDevicePowerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "qos-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
        _store = new TestableConfigStore(_settingsPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void SetPower_Off_AddsToDisabledList()
    {
        var provider = new StubDeviceProvider(_store);
        provider.SetPower("openrgb-3", false);

        Assert.Contains("openrgb-3", _store.Load().Devices.DisabledLightingDevices);
    }

    [Fact]
    public void SetPower_On_RemovesFromDisabledList()
    {
        var provider = new StubDeviceProvider(_store);
        provider.SetPower("openrgb-3", false);
        provider.SetPower("openrgb-3", true);

        Assert.DoesNotContain("openrgb-3", _store.Load().Devices.DisabledLightingDevices);
    }

    [Fact]
    public void SetPower_Off_IsIdempotent()
    {
        var provider = new StubDeviceProvider(_store);
        provider.SetPower("openrgb-3", false);
        provider.SetPower("openrgb-3", false);

        var list = _store.Load().Devices.DisabledLightingDevices;
        Assert.Single(list, id => id == "openrgb-3");
    }

    [Fact]
    public void SetPower_OnlyTouchesGivenId()
    {
        var provider = new StubDeviceProvider(_store);
        provider.SetPower("openrgb-0", false);
        provider.SetPower("openrgb-1", false);
        provider.SetPower("openrgb-0", true);

        var list = _store.Load().Devices.DisabledLightingDevices;
        Assert.DoesNotContain("openrgb-0", list);
        Assert.Contains("openrgb-1", list);
    }
}
