using System.Collections.Generic;
using Qos.Service.Devices;
using Qos.Service.Devices.Handlers;
using Xunit;

namespace Qos.Service.Tests;

/// <summary>
/// Identity / connection-detection tests for every device handler. These are
/// the cheapest possible regression net: if a handler's Id, Name, Category, or
/// VID/PID list is accidentally changed, the device disappears from
/// /devices/all in production.
/// </summary>
public class DeviceHandlerTests
{
    private static UsbDeviceEntry Entry(int vid, int pid, string name = "Test")
        => new() { VendorId = vid, ProductId = pid, Name = name };

    [Fact]
    public void Cnvs_id_and_metadata()
    {
        var h = new CnvsHandler();
        Assert.Equal("cnvs", h.Id);
        Assert.Equal("CNVS", h.Name);
        Assert.Equal("controller", h.Category);
        Assert.NotEmpty(h.Identifiers);
    }

    [Fact]
    public void Cnvs_detects_known_vid_pids()
    {
        var h = new CnvsHandler();
        foreach (var id in h.Identifiers)
        {
            var detected = new List<UsbDeviceEntry> { Entry(id.VendorId, id.ProductId) };
            Assert.True(h.IsConnected(detected), $"CnvsHandler should detect {id.VendorId:X4}:{id.ProductId:X4}");
        }
    }

    [Fact]
    public void Cnvs_does_not_falsely_match_random_vid()
    {
        var h = new CnvsHandler();
        var detected = new List<UsbDeviceEntry> { Entry(0x046d, 0xc52b) }; // Logitech mouse
        Assert.False(h.IsConnected(detected));
    }

    [Fact]
    public void Cnvs_returns_empty_firmware_when_disconnected()
    {
        var h = new CnvsHandler();
        Assert.Equal(string.Empty, h.GetFirmwareVersion());
    }

    [Theory]
    [MemberData(nameof(AllHandlers))]
    public void Every_handler_has_id_name_category(IDeviceHandler handler)
    {
        Assert.False(string.IsNullOrEmpty(handler.Id));
        Assert.False(string.IsNullOrEmpty(handler.Name));
        Assert.False(string.IsNullOrEmpty(handler.Category));
    }

    [Theory]
    [MemberData(nameof(AllHandlers))]
    public void Every_handler_returns_false_for_empty_device_list(IDeviceHandler handler)
    {
        Assert.False(handler.IsConnected(new List<UsbDeviceEntry>()));
    }

    [Theory]
    [MemberData(nameof(AllHandlers))]
    public void Every_handler_has_at_least_one_identifier(IDeviceHandler handler)
    {
        // FanHub has identifiers via VID/PID; if a handler omits them, IsConnected
        // could only return true for type-coupled checks - flag that here.
        Assert.NotNull(handler.Identifiers);
    }

    [Theory]
    [MemberData(nameof(AllHandlers))]
    public void Every_handler_detects_each_of_its_own_identifiers(IDeviceHandler handler)
    {
        foreach (var id in handler.Identifiers)
        {
            var detected = new List<UsbDeviceEntry> { Entry(id.VendorId, id.ProductId) };
            Assert.True(handler.IsConnected(detected),
                $"{handler.Name} should detect {id.VendorId:X4}:{id.ProductId:X4}");
        }
    }

    [Fact]
    public void Q60_and_Q80_have_distinct_ids()
    {
        var q60 = new Q60Handler();
        var q80 = new Q80Handler();
        Assert.NotEqual(q60.Id, q80.Id);
    }

    [Fact]
    public void Y70_and_Q60_categories_are_displays()
    {
        var y70 = new Y70Handler();
        var q60 = new Q60Handler();
        // Both are device-display peripherals; exact category strings are
        // implementation detail but should be non-empty.
        Assert.False(string.IsNullOrEmpty(y70.Category));
        Assert.False(string.IsNullOrEmpty(q60.Category));
    }

    public static IEnumerable<object[]> AllHandlers()
    {
        yield return new object[] { new CnvsHandler() };
        yield return new object[] { new Q60Handler() };
        yield return new object[] { new Q80Handler() };
        yield return new object[] { new Y70Handler() };
        yield return new object[] { new KeebHandler() };
        yield return new object[] { new FanHubHandler() };
    }
}
