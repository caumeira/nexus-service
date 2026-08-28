using System;
using System.Linq;
using Nexus.Service.Peripherals.Nollie;
using Xunit;
using FakeHidDevice = Nexus.Service.Tests.Nollie.NollieLightingDeviceProviderTests.FakeHidDevice;

namespace Nexus.Service.Tests.Nollie;

/// <summary>What actually reaches the wire: chunking, latching, and the multi-controller set.</summary>
public class NollieHubTests
{
    private static (NollieController controller, FakeHidDevice device) Make(int vid, int pid, string serial = "SN")
    {
        var spec = NollieProtocol.Lookup(vid, pid)!;
        var device = new FakeHidDevice(vid, pid, $"path-{serial}", serial);
        return (new NollieController(device, spec), device);
    }

    [Fact]
    public void Wide_transport_sends_one_report_per_channel()
    {
        var (c, dev) = Make(0x16D5, 0x2A16);
        c.SendChannel(cardIndex: 0, new byte[30 * 3]);
        var report = Assert.Single(dev.Writes);
        Assert.Equal(NollieProtocol.WideReportSize, report.Length);
        Assert.Equal(3, report[1]); // card 0 maps to hardware channel 3
        Assert.Equal(30, report[4]);
    }

    /// <summary>21 LEDs per 65-byte report, so 50 LEDs needs three.</summary>
    [Fact]
    public void Chunked_transport_splits_into_21_led_reports()
    {
        var (c, dev) = Make(0x16D5, 0x2A01);
        c.SendChannel(0, new byte[50 * 3]);
        Assert.Equal(3, dev.Writes.Count);
        Assert.All(dev.Writes, w => Assert.Equal(NollieProtocol.ChunkedReportSize, w.Length));
        Assert.Equal(0, dev.Writes[0][1]);
        Assert.Equal(1, dev.Writes[1][1]);
        Assert.Equal(2, dev.Writes[2][1]);
    }

    [Fact]
    public void Chunked_transport_sends_nothing_for_an_empty_channel()
    {
        var (c, dev) = Make(0x16D5, 0x2A01);
        c.SendChannel(0, ReadOnlySpan<byte>.Empty);
        Assert.Empty(dev.Writes);
    }

    [Fact]
    public void Latch_applies_only_to_the_chunked_transport()
    {
        var (chunked, chunkedDev) = Make(0x16D5, 0x2A01);
        Assert.True(chunked.SendLatch());
        Assert.Equal(0xFF, Assert.Single(chunkedDev.Writes)[1]);

        var (wide, wideDev) = Make(0x16D5, 0x2A16);
        Assert.False(wide.SendLatch());
        Assert.Empty(wideDev.Writes);
    }

    [Fact]
    public void Led_count_handshake_only_reaches_the_legacy_1ch_part()
    {
        var (os2, os2Dev) = Make(0x16D5, 0x2A01);
        Assert.False(os2.SendLedCounts(new[] { 30 }));
        Assert.Empty(os2Dev.Writes);

        var (legacy, legacyDev) = Make(0x16D2, 0x1F11);
        Assert.True(legacy.SendLedCounts(new[] { 30 }));
        Assert.Equal(0xFE, Assert.Single(legacyDev.Writes)[1]);
    }

    [Fact]
    public void Write_failures_accumulate_and_reset_on_success()
    {
        var (c, dev) = Make(0x16D5, 0x2A16);
        dev.WriteResult = false;
        c.SendChannel(0, new byte[3]);
        c.SendChannel(0, new byte[3]);
        Assert.Equal(2, c.ConsecutiveWriteFailures);

        dev.WriteResult = true;
        c.SendChannel(0, new byte[3]);
        Assert.Equal(0, c.ConsecutiveWriteFailures);
    }

    [Fact]
    public void Disposed_controller_stops_writing()
    {
        var (c, dev) = Make(0x16D5, 0x2A16);
        c.Dispose();
        Assert.False(c.SendChannel(0, new byte[3]));
        Assert.Empty(dev.Writes);
    }

    // ── Hub set ──

    [Fact]
    public void Hub_holds_several_controllers_and_orders_them_stably()
    {
        var hub = new NollieHub();
        hub.Attach(Make(0x16D5, 0x2A01, "BBB").controller);
        hub.Attach(Make(0x16D5, 0x2A01, "AAA").controller);
        Assert.Equal(new[] { "nollie-s-AAA", "nollie-s-BBB" }, hub.Controllers.Select(c => c.DeviceId));
        Assert.True(hub.IsConnected);
    }

    [Fact]
    public void Reattaching_the_same_id_replaces_rather_than_duplicates()
    {
        var hub = new NollieHub();
        hub.Attach(Make(0x16D5, 0x2A01, "AAA").controller);
        var second = Make(0x16D5, 0x2A01, "AAA").controller;
        hub.Attach(second);
        Assert.Same(second, Assert.Single(hub.Controllers));
    }

    [Fact]
    public void HasPath_tracks_the_attached_handles()
    {
        var hub = new NollieHub();
        var (c, _) = Make(0x16D5, 0x2A01, "AAA");
        hub.Attach(c);
        Assert.True(hub.HasPath("path-AAA"));
        Assert.False(hub.HasPath("path-ZZZ"));
        hub.Detach(c.DeviceId);
        Assert.False(hub.HasPath("path-AAA"));
        Assert.False(hub.IsConnected);
    }

    [Fact]
    public void Find_resolves_by_device_id()
    {
        var hub = new NollieHub();
        var (c, _) = Make(0x16D5, 0x2A01, "AAA");
        hub.Attach(c);
        Assert.Same(c, hub.Find(c.DeviceId));
        Assert.Null(hub.Find("nollie-s-NOPE"));
    }
}
