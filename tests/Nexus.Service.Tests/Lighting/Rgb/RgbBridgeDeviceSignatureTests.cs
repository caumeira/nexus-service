using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests.Lighting.Rgb;

/// <summary>Change key and de-duplication for the settled-device log.</summary>
public class RgbBridgeDeviceSignatureTests
{
    private static RgbDevice Device(int index = 0, string name = "Corsair K100 RGB Optical",
        int ledCount = 173, string serial = "F50019035E77A37BAB6C50481302F03C",
        string location = @"HID: \\?\hid#vid_1b1c&pid_1b7c&mi_01&col01#8&2d59ea83&0&0000") =>
        new() { Index = index, Name = name, LedCount = ledCount, Serial = serial, Location = location };

    [Fact]
    public void SameList_ProducesSameSignature()
    {
        Assert.Equal(
            RgbBridge.BuildDeviceSignature(new[] { Device() }),
            RgbBridge.BuildDeviceSignature(new[] { Device() }));
    }

    [Fact]
    public void DifferentCollectionOnSameDevice_ChangesSignature()
    {
        var col01 = RgbBridge.BuildDeviceSignature(new[] { Device() });
        var col02 = RgbBridge.BuildDeviceSignature(new[]
        {
            Device(location: @"HID: \\?\hid#vid_1b1c&pid_1b7c&mi_01&col02#8&2d59ea83&0&0000"),
        });
        Assert.NotEqual(col01, col02);
    }

    [Fact]
    public void LedCountChange_ChangesSignature()
    {
        Assert.NotEqual(
            RgbBridge.BuildDeviceSignature(new[] { Device() }),
            RgbBridge.BuildDeviceSignature(new[] { Device(ledCount: 0) }));
    }

    [Fact]
    public void AddedDevice_ChangesSignature()
    {
        Assert.NotEqual(
            RgbBridge.BuildDeviceSignature(new[] { Device() }),
            RgbBridge.BuildDeviceSignature(new[] { Device(), Device(index: 1, name: "ENE DRAM", serial: "") }));
    }

    [Fact]
    public void EmptyList_IsStable()
    {
        Assert.Equal("", RgbBridge.BuildDeviceSignature(Array.Empty<RgbDevice>()));
    }

    [Fact]
    public void SettlingOnZeroDevices_Logs()
    {
        // The empty signature is a shape like any other: a boot that finds nothing
        // has to be distinguishable from a boot that never settled.
        var logged = new HashSet<string>(StringComparer.Ordinal);
        Assert.True(RgbBridge.TryMarkLogged(logged, RgbBridge.BuildDeviceSignature(Array.Empty<RgbDevice>())));
    }

    [Fact]
    public void RepeatedSignature_LogsOnce()
    {
        var logged = new HashSet<string>(StringComparer.Ordinal);
        Assert.True(RgbBridge.TryMarkLogged(logged, "a"));
        Assert.False(RgbBridge.TryMarkLogged(logged, "a"));
    }

    [Fact]
    public void FlappingBetweenTwoShapes_LogsEachOnce()
    {
        var logged = new HashSet<string>(StringComparer.Ordinal);
        Assert.True(RgbBridge.TryMarkLogged(logged, "a"));
        Assert.True(RgbBridge.TryMarkLogged(logged, "b"));
        for (var i = 0; i < 50; i++)
        {
            Assert.False(RgbBridge.TryMarkLogged(logged, i % 2 == 0 ? "a" : "b"));
        }
    }

    [Fact]
    public void UnboundedChurn_DoesNotGrowTheSet()
    {
        var logged = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 500; i++)
        {
            Assert.True(RgbBridge.TryMarkLogged(logged, $"shape-{i}"));
        }
        Assert.InRange(logged.Count, 1, 16);
    }
}
