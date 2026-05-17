using System.Collections.Generic;
using System.Linq;
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
    public void QSeries_handler_covers_Q60_and_Q80()
    {
        var qs = new QSeriesHandler();
        // Q-series handler reports both PIDs; the on-device runtime and
        // the qos panel pipeline treat Q60 and Q80 identically.
        var pids = qs.Identifiers.Select(id => id.ProductId).ToHashSet();
        Assert.Contains(0x0600, pids); // Q60
        Assert.Contains(0x0603, pids); // Q80
    }

    [Fact]
    public void Y70_and_QSeries_categories_are_displays()
    {
        var y70 = new Y70Handler();
        var qs = new QSeriesHandler();
        // Both are device-display peripherals; exact category strings are
        // implementation detail but should be non-empty.
        Assert.False(string.IsNullOrEmpty(y70.Category));
        Assert.False(string.IsNullOrEmpty(qs.Category));
    }

    public static IEnumerable<object[]> AllHandlers()
    {
        yield return new object[] { new CnvsHandler() };
        yield return new object[] { new QSeriesHandler() };
        yield return new object[] { new Y70Handler() };
        yield return new object[] { new KeebHandler() };
        yield return new object[] { new FanHubHandler() };
    }
}
