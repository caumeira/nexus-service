using Nexus.Service.Devices;
using Nexus.Service.QSeries;
using Xunit;

namespace Nexus.Service.Tests.QSeries;

/// <summary>
/// The devnode-reset recovery identifies its target by USB product name when the
/// panel was never seen online - a service start that finds the MediaTek driver
/// already bound to the composite parent, leaving no adb device line to read.
/// Strings are the bench-verified descriptors documented on QSeriesHandler, which
/// include the "HYTE THICC Q60" form the panel reports in some USB modes.
/// </summary>
public class QSeriesUsbIdentityTests
{
    private const int MediaTekVid = 0x0E8D;

    private static UsbDeviceEntry Entry(string name, int vid = MediaTekVid) =>
        new() { Name = name, VendorId = vid };

    [Theory]
    [InlineData("HYTE Q60 Display")]
    [InlineData("HYTE THICC Q60")]
    [InlineData("HYTE Q80 Display")]
    [InlineData("HYTE THICC Q80")]
    public void Every_bench_verified_panel_descriptor_identifies_the_panel(string name) =>
        Assert.True(QSeriesPortWatcher.IsQSeriesUsbEntry(Entry(name)));

    [Theory]
    [InlineData("")]
    [InlineData("Redmi Note 12")]
    [InlineData("MT65xx Android Phone")]
    public void Anything_else_on_the_MediaTek_vendor_id_is_not_a_panel(string name) =>
        Assert.False(QSeriesPortWatcher.IsQSeriesUsbEntry(Entry(name)));

    [Theory]
    [InlineData("Q60 Tablet")]
    [InlineData("Generic Q80 Device")]
    public void A_bare_model_substring_without_the_vendor_name_does_not_authorise_a_reset(string name) =>
        Assert.False(QSeriesPortWatcher.IsQSeriesUsbEntry(Entry(name)));

    [Fact]
    public void A_panel_name_on_an_unrelated_vendor_id_is_not_a_panel() =>
        Assert.False(QSeriesPortWatcher.IsQSeriesUsbEntry(Entry("HYTE Q60 Display", vid: 0x1234)));

    [Fact]
    public void A_missing_name_is_not_a_panel() =>
        Assert.False(QSeriesPortWatcher.IsQSeriesUsbEntry(new UsbDeviceEntry { VendorId = MediaTekVid }));
}

/// <summary>
/// Guards which serial the devnode reset addresses, and what may be interpolated
/// into the instance-id query. A composite interface node can inherit the panel's
/// product name while its instance id ends in a parent-id token, which would point
/// the reset at the wrong devnode.
/// </summary>
public class DeviceSerialShapeTests
{
    [Theory]
    [InlineData("0123456789ABCDEF")]
    [InlineData("205939A94E31")]
    public void A_real_usb_serial_is_addressable(string serial) =>
        Assert.True(QSeriesPortWatcher.LooksLikeDeviceSerial(serial));

    [Theory]
    [InlineData("7&1a2b3c&0&0000")]
    [InlineData("")]
    public void A_parent_id_token_or_a_missing_serial_is_not(string serial) =>
        Assert.False(QSeriesPortWatcher.LooksLikeDeviceSerial(serial));

    [Theory]
    [InlineData("abc'; Start-Process calc.exe; '")]
    [InlineData("abc*")]
    [InlineData("abc?")]
    [InlineData("abc[0-9]")]
    [InlineData("abc def")]
    [InlineData("abc\\")]
    public void A_serial_that_could_escape_or_widen_the_instance_id_query_is_rejected(string serial) =>
        Assert.False(QSeriesPortWatcher.LooksLikeDeviceSerial(serial));

    [Fact]
    public void A_tcp_transport_serial_is_not_a_devnode_serial() =>
        Assert.False(QSeriesPortWatcher.LooksLikeDeviceSerial("192.168.1.42:5555"));
}
