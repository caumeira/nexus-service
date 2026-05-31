using Nexus.Service.Peripherals.Hid;

namespace Nexus.Service.Tests.Keeb;

/// <summary>
/// HID report-descriptor parsing for the Linux hidraw enumerator. The byte
/// vectors are the ACTUAL descriptors read from the HYTE Keeb TKL on Linux
/// (3402:0300, /sys/class/hidraw/hidrawN/device/report_descriptor) — so this
/// pins the logic that selects the vendor collection (usage page 0xFF11 /
/// usage 0xF0) over the keyboard / mouse / other-vendor collections.
/// </summary>
public class LinuxHidParseTests
{
    [Fact]
    public void ParseTopUsage_keyboard_collection()
    {
        // hidraw0: Usage Page (Generic Desktop) 0x01, Usage (Keyboard) 0x06.
        var d = new byte[] { 0x05, 0x01, 0x09, 0x06, 0xa1, 0x01, 0x05, 0x07 };
        Assert.Equal((0x01, 0x06), LinuxHidEnumerator.ParseTopUsage(d));
    }

    [Fact]
    public void ParseTopUsage_mouse_collection()
    {
        // hidraw1: Usage Page 0x01, Usage (Mouse) 0x02.
        var d = new byte[] { 0x05, 0x01, 0x09, 0x02, 0xa1, 0x01, 0x85, 0x02 };
        Assert.Equal((0x01, 0x02), LinuxHidEnumerator.ParseTopUsage(d));
    }

    [Fact]
    public void ParseTopUsage_selects_vendor_FF11_F0()
    {
        // hidraw2: the protocol interface — Usage Page 0xFF11, Usage 0xF0.
        var d = new byte[] { 0x06, 0x11, 0xff, 0x09, 0xf0, 0xa1, 0x01, 0x15, 0x00 };
        Assert.Equal((0xFF11, 0xF0), LinuxHidEnumerator.ParseTopUsage(d));
    }

    [Fact]
    public void ParseTopUsage_other_vendor_collection_FF10()
    {
        // hidraw3: a different vendor collection — Usage Page 0xFF10, Usage 0x01.
        var d = new byte[] { 0x06, 0x10, 0xff, 0x09, 0x01, 0xa1, 0x01, 0x15, 0x00 };
        Assert.Equal((0xFF10, 0x01), LinuxHidEnumerator.ParseTopUsage(d));
    }
}
