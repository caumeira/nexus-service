using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests.Lighting.Mappings;

public class DeviceKeyComputerTests
{
    [Fact]
    public void Windows_hid_location_yields_usb_key()
    {
        var device = new RgbDevice
        {
            Name = "Corsair Lighting Node CORE",
            Vendor = "Corsair",
            Location = @"HID: \\?\hid#vid_1B1C&pid_0C1A&mi_00#8&2de99099&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}",
        };
        Assert.Equal("usb:1b1c:0c1a", DeviceKeyComputer.ForOpenRgbDevice(device));
    }

    [Fact]
    public void Non_usb_device_falls_back_to_model_hash()
    {
        var device = new RgbDevice
        {
            Name = "ASUS ROG STRIX B850-E GAMING",
            Vendor = "ASUS",
            Location = "i2c-12",
        };
        var key = DeviceKeyComputer.ForOpenRgbDevice(device);
        Assert.StartsWith("orgb:", key);
        Assert.Equal(21, key.Length);
    }

    [Fact]
    public void Model_hash_is_stable_across_case_and_whitespace()
    {
        var a = new RgbDevice { Name = "ASUS ROG  Strix", Vendor = "ASUS", Location = "" };
        var b = new RgbDevice { Name = "  asus rog strix ", Vendor = "asus", Location = "" };
        Assert.Equal(DeviceKeyComputer.ForOpenRgbDevice(a), DeviceKeyComputer.ForOpenRgbDevice(b));
    }

    [Fact]
    public void Different_models_hash_differently()
    {
        var a = new RgbDevice { Name = "Model A", Vendor = "V", Location = "" };
        var b = new RgbDevice { Name = "Model B", Vendor = "V", Location = "" };
        Assert.NotEqual(DeviceKeyComputer.ForOpenRgbDevice(a), DeviceKeyComputer.ForOpenRgbDevice(b));
    }

    [Fact]
    public void Zone_key_appends_zone_suffix()
        => Assert.Equal("usb:1b1c:0c1a:zone:2", DeviceKeyComputer.ForZone("usb:1b1c:0c1a", 2));

    [Fact]
    public void First_party_key_includes_module_discriminator()
    {
        Assert.Equal("usb:3402:0901", DeviceKeyComputer.ForFirstParty(0x3402, 0x0901));
        Assert.Equal("usb:3402:0901:ls10", DeviceKeyComputer.ForFirstParty(0x3402, 0x0901, "LS10"));
        Assert.Equal("usb:3402:0901:logo", DeviceKeyComputer.ForFirstParty(0x3402, 0x0901, "logo"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("i2c-5")]
    [InlineData("vid_12")]
    [InlineData("vid_zzzz&pid_0c1a")]
    public void Unparseable_locations_do_not_yield_usb_ids(string? location)
        => Assert.False(DeviceKeyComputer.TryParseUsbIdsFromLocation(location, out _, out _));
}
