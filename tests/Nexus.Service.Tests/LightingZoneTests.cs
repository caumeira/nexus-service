using Nexus.Service.Devices;
using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests;

public class LightingZoneTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;
    private readonly TestableConfigStore _store;

    public LightingZoneTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
        _store = new TestableConfigStore(_settingsPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void SetZoneLedCount_PersistsInStore()
    {
        var provider = new StubDeviceProvider(_store);
        provider.SetZoneLedCount("openrgb-0-1", 16);

        var stored = _store.Load().Devices.ZoneLedCounts;
        Assert.True(stored.ContainsKey("openrgb-0-1"));
        Assert.Equal(16, stored["openrgb-0-1"]);
    }

    [Fact]
    public void SetZoneLedCount_UpdatesExistingEntry()
    {
        var provider = new StubDeviceProvider(_store);
        provider.SetZoneLedCount("openrgb-0-0", 8);
        provider.SetZoneLedCount("openrgb-0-0", 20);

        Assert.Equal(20, _store.Load().Devices.ZoneLedCounts["openrgb-0-0"]);
    }

    [Fact]
    public void SetZoneLedCount_OnlyTouchesGivenId()
    {
        var provider = new StubDeviceProvider(_store);
        provider.SetZoneLedCount("openrgb-0-0", 8);
        provider.SetZoneLedCount("openrgb-0-1", 16);

        var counts = _store.Load().Devices.ZoneLedCounts;
        Assert.Equal(8, counts["openrgb-0-0"]);
        Assert.Equal(16, counts["openrgb-0-1"]);
    }

    [Fact]
    public void SetZoneLedCount_RejectsNegative()
    {
        var provider = new StubDeviceProvider(_store);
        provider.SetZoneLedCount("openrgb-0-0", -5);

        Assert.False(_store.Load().Devices.ZoneLedCounts.ContainsKey("openrgb-0-0"));
    }

    [Fact]
    public void Identify_OnStubIsNoOp()
    {
        var provider = new StubDeviceProvider(_store);
        provider.Identify("openrgb-0-0", 2000);
        // Stub has no side effect we can assert - just verifies no throw.
    }

    [Fact]
    public void BuildResizeZoneBody_LayoutMatchesProtocol()
    {
        var body = OpenRgbProtocol.BuildResizeZoneBody(zoneIndex: 2, newSize: 16);
        // 4 bytes zone_index + 4 bytes new_size
        Assert.Equal(8, body.Length);
        // zone_index = 2 (int32 little-endian)
        Assert.Equal(2, body[0]);
        Assert.Equal(0, body[1]);
        Assert.Equal(0, body[2]);
        Assert.Equal(0, body[3]);
        // new_size = 16 (uint32 little-endian)
        Assert.Equal(16, body[4]);
        Assert.Equal(0, body[5]);
    }

    [Fact]
    public void BuildUpdateZoneLedsBody_LayoutMatchesProtocol()
    {
        var colors = new[] { new RgbColor(255, 128, 64), new RgbColor(0, 0, 0) };
        var body = OpenRgbProtocol.BuildUpdateZoneLedsBody(zoneIndex: 3, colors);

        // 4 (data_size) + 4 (zone_index) + 2 (led_count) + 2 * 4 = 18
        Assert.Equal(18, body.Length);

        // data_size = 18 (uint32 little-endian)
        Assert.Equal(18, body[0]);
        // zone_index = 3 (uint32 little-endian)
        Assert.Equal(3, body[4]);
        // led_count = 2 (uint16 little-endian)
        Assert.Equal(2, body[8]);
        Assert.Equal(0, body[9]);
        // LED 0: R=255, G=128, B=64, pad=0
        Assert.Equal(255, body[10]);
        Assert.Equal(128, body[11]);
        Assert.Equal(64, body[12]);
        Assert.Equal(0, body[13]);
        // LED 1: R=0, G=0, B=0, pad=0
        Assert.Equal(0, body[14]);
        Assert.Equal(0, body[15]);
        Assert.Equal(0, body[16]);
    }

    [Fact]
    public void ZoneOpcodesHaveExpectedValues()
    {
        // These are the OpenRGB NetworkProtocol.h SDK opcodes. Hard-code the
        // integer values so a rename on our side can't accidentally change
        // what we send on the wire.
        Assert.Equal(1000u, (uint)OpenRgbProtocol.PacketId.RgbControllerResizeZone);
        Assert.Equal(1051u, (uint)OpenRgbProtocol.PacketId.RgbControllerUpdateZoneLeds);
    }
}
