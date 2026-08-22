using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Hyte.Keeb;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Keeb;

/// <summary>
/// The brightness knob is followed through the input worker's own HID handle
/// rather than by polling the settings page, because that poll shared the LED
/// stream's IO lock and stalled frames (camera-measured flicker). These pin the
/// replacement: the detent must move BOTH the stored firmware byte and master
/// brightness, and must ignore an encoder assigned to something else.
/// </summary>
public class KeebKnobBrightnessTests
{
    // HandleEncoderScroll never touches the device - it is the whole point of
    // the change - so an enumerator that finds nothing is enough.
    private sealed class NoDevices : IHidEnumerator
    {
        public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) => System.Array.Empty<HidDeviceInfo>();
        public IReadOnlyList<HidDeviceInfo> FindAll() => System.Array.Empty<HidDeviceInfo>();
        public IHidDevice? Open(string path, bool forInput = false) => null;
    }

    private static KeebSettingsApplier Applier(IConfigStore store) =>
        new(new KeebHub(new NoDevices()), store, new Nexus.Service.Sockets.MultiplexHub());

    private static IConfigStore StoreWith(string left, string right, int brightness)
    {
        var store = new Nexus.Service.Tests.InMemoryConfigStore();
        store.Update(s =>
        {
            s.Keeb.RotaryLeft = left;
            s.Keeb.RotaryRight = right;
            s.Keeb.FirmwareLighting.Brightness = brightness;
            s.Lighting.GlobalBrightness = brightness / 100f;
        });
        return store;
    }

    [Fact]
    public void A_detent_on_the_brightness_encoder_moves_master_and_the_stored_byte()
    {
        var store = StoreWith("VolumeAdjustment", "BrightnessAdjustment", 50);
        var handled = Applier(store).HandleEncoderScroll(KeebProtocol.KeebEncoder.Right, up: true);
        Assert.True(handled);
        var s = store.Load();
        Assert.Equal(55, s.Keeb.FirmwareLighting.Brightness);
        Assert.Equal(0.55f, s.Lighting.GlobalBrightness, 3);
    }

    [Fact]
    public void Scrolling_down_lowers_it()
    {
        var store = StoreWith("BrightnessAdjustment", "VolumeAdjustment", 50);
        Applier(store).HandleEncoderScroll(KeebProtocol.KeebEncoder.Left, up: false);
        Assert.Equal(45, store.Load().Keeb.FirmwareLighting.Brightness);
    }

    [Fact]
    public void An_encoder_assigned_to_volume_is_left_alone()
    {
        var store = StoreWith("VolumeAdjustment", "BrightnessAdjustment", 50);
        var handled = Applier(store).HandleEncoderScroll(KeebProtocol.KeebEncoder.Left, up: true);
        Assert.False(handled);
        Assert.Equal(50, store.Load().Keeb.FirmwareLighting.Brightness);
    }

    [Theory]
    [InlineData(98, true, 100)]
    [InlineData(2, false, 0)]
    public void It_clamps_at_the_ends(int start, bool up, int expected)
    {
        var store = StoreWith("VolumeAdjustment", "BrightnessAdjustment", start);
        Applier(store).HandleEncoderScroll(KeebProtocol.KeebEncoder.Right, up);
        Assert.Equal(expected, store.Load().Keeb.FirmwareLighting.Brightness);
    }
}
