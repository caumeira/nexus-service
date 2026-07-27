using Nexus.Service.Devices;
using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests.Lighting.Rgb;

/// <summary>
/// The USB hot-plug relevance filter: a topology diff only warrants an OpenRGB
/// subprocess bounce when a changed device's vendor id is RGB-capable. An empty
/// vendor catalog fails open to the legacy bounce-on-anything behavior.
/// </summary>
public class UsbTopologyFilterTests
{
    private static UsbDeviceEntry Entry(int vid, int pid, string name, string serial = "") =>
        new() { VendorId = vid, ProductId = pid, Name = name, Serial = serial };

    private static readonly HashSet<int> RgbVendors = new() { 0x1B1C, 0x3402, 0x0CF2 };

    [Fact]
    public void No_change_reports_no_diff()
    {
        var keys = UsbTopologyFilter.BuildKeys(new[] { Entry(0x1B1C, 0x0C3F, "iCUE Hub"), Entry(0x05AC, 0x024F, "Keyboard") });
        var (added, removed, _) = UsbTopologyFilter.Classify(keys, keys, RgbVendors);
        Assert.Equal(0, added);
        Assert.Equal(0, removed);
    }

    [Fact]
    public void Irrelevant_vendor_change_is_not_relevant()
    {
        var before = UsbTopologyFilter.BuildKeys(new[] { Entry(0x1B1C, 0x0C3F, "iCUE Hub") });
        var after = UsbTopologyFilter.BuildKeys(new[] { Entry(0x1B1C, 0x0C3F, "iCUE Hub"), Entry(0x0781, 0x5581, "Ultra USB stick") });
        var (added, removed, relevant) = UsbTopologyFilter.Classify(before, after, RgbVendors);
        Assert.Equal(1, added);
        Assert.Equal(0, removed);
        Assert.False(relevant);
    }

    [Fact]
    public void Rgb_vendor_arrival_is_relevant()
    {
        var before = UsbTopologyFilter.BuildKeys(new[] { Entry(0x0781, 0x5581, "Ultra USB stick") });
        var after = UsbTopologyFilter.BuildKeys(new[] { Entry(0x0781, 0x5581, "Ultra USB stick"), Entry(0x0CF2, 0xA102, "Uni Fan") });
        var (added, _, relevant) = UsbTopologyFilter.Classify(before, after, RgbVendors);
        Assert.Equal(1, added);
        Assert.True(relevant);
    }

    [Fact]
    public void Rgb_vendor_removal_is_relevant()
    {
        var before = UsbTopologyFilter.BuildKeys(new[] { Entry(0x3402, 0x0901, "NP50") });
        var after = UsbTopologyFilter.BuildKeys(System.Array.Empty<UsbDeviceEntry>());
        var (_, removed, relevant) = UsbTopologyFilter.Classify(before, after, RgbVendors);
        Assert.Equal(1, removed);
        Assert.True(relevant);
    }

    [Fact]
    public void Equal_count_swap_is_seen_as_change()
    {
        // The legacy count-based check was blind to a simultaneous plug+unplug.
        var before = UsbTopologyFilter.BuildKeys(new[] { Entry(0x0781, 0x5581, "Ultra USB stick") });
        var after = UsbTopologyFilter.BuildKeys(new[] { Entry(0x1B1C, 0x1B5E, "K70 Keyboard") });
        var (added, removed, relevant) = UsbTopologyFilter.Classify(before, after, RgbVendors);
        Assert.Equal(1, added);
        Assert.Equal(1, removed);
        Assert.True(relevant);
    }

    [Fact]
    public void Empty_vendor_catalog_fails_open()
    {
        var before = UsbTopologyFilter.BuildKeys(System.Array.Empty<UsbDeviceEntry>());
        var after = UsbTopologyFilter.BuildKeys(new[] { Entry(0x0781, 0x5581, "Ultra USB stick") });
        var (_, _, relevant) = UsbTopologyFilter.Classify(before, after, new HashSet<int>());
        Assert.True(relevant);
    }

    [Fact]
    public void Second_identical_serialless_unit_is_seen_as_change()
    {
        // Cheap hubs ship without serials; per-key counting keeps the old
        // count-based sensitivity the set-diff form would lose.
        var one = UsbTopologyFilter.BuildKeys(new[] { Entry(0x0CF2, 0xA102, "Uni Fan") });
        var two = UsbTopologyFilter.BuildKeys(new[] { Entry(0x0CF2, 0xA102, "Uni Fan"), Entry(0x0CF2, 0xA102, "Uni Fan") });
        var (added, removed, relevant) = UsbTopologyFilter.Classify(one, two, RgbVendors);
        Assert.Equal(1, added);
        Assert.Equal(0, removed);
        Assert.True(relevant);

        var (added2, removed2, relevant2) = UsbTopologyFilter.Classify(two, one, RgbVendors);
        Assert.Equal(0, added2);
        Assert.Equal(1, removed2);
        Assert.True(relevant2);
    }

    [Fact]
    public void Identical_devices_differing_only_by_serial_are_distinct()
    {
        var before = UsbTopologyFilter.BuildKeys(new[] { Entry(0x0CF2, 0xA102, "Uni Fan", "A"), Entry(0x0CF2, 0xA102, "Uni Fan", "B") });
        var after = UsbTopologyFilter.BuildKeys(new[] { Entry(0x0CF2, 0xA102, "Uni Fan", "A") });
        var (added, removed, relevant) = UsbTopologyFilter.Classify(before, after, RgbVendors);
        Assert.Equal(0, added);
        Assert.Equal(1, removed);
        Assert.True(relevant);
    }
}
