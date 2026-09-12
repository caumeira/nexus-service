using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Cooling;
using Nexus.Service.Peripherals.Hyte.MiniHub;
using Nexus.Service.Peripherals.Hyte.Np50;
using Xunit;

namespace Nexus.Service.Tests.MiniHub;

public class MiniHubCoolingProviderRpmTests
{
    // Firmware 1.0.1.1 returns a period byte that never tracks the fan, so the
    // decoded number must not reach a card even when the poll stored one.
    [Fact]
    public void GetFanChannels_marks_both_ports_rpm_unavailable_and_hides_the_decoded_value()
    {
        var hub = NewConnectedHub();
        hub.State.Port1Fans = 1;
        hub.State.Port2Fans = 3;
        hub.State.Port1Rpm = 18750;
        hub.State.Port2Rpm = 150000;

        var channels = new MiniHubCoolingProvider(hub).GetFanChannels();

        Assert.Equal(2, channels.Count);
        Assert.All(channels, c => Assert.True(c.RpmUnavailable));
        Assert.All(channels, c => Assert.Equal(0, c.Rpm));
    }

    [Fact]
    public void GetAll_reports_no_rpm_for_the_cooling_devices()
    {
        var hub = NewConnectedHub();
        hub.State.Port1Fans = 1;
        hub.State.Port2Fans = 3;
        hub.State.Port1Rpm = 18750;
        hub.State.Port2Rpm = 150000;

        var devices = new MiniHubCoolingProvider(hub).GetAll().SelectMany(c => c.Devices).ToList();

        Assert.Equal(2, devices.Count);
        Assert.All(devices, d => Assert.Null(d.Rpm));
    }

    private static MiniHubHub NewConnectedHub()
    {
        var hub = new MiniHubHub(new FakeDiscovery(), _ => new FakeTransport());
        Assert.True(hub.EnsureConnected());
        return hub;
    }

    private sealed class FakeDiscovery : INp50PortDiscovery
    {
        public IReadOnlyList<Np50PortInfo> Discover()
            => new[] { new Np50PortInfo { PortName = "COM_TEST", Serial = "MHTEST" } };
    }

    private sealed class FakeTransport : INp50Transport
    {
        public bool IsOpen => true;
        public string Serial => "MHTEST";
        public void DiscardInput() { }
        public void Write(ReadOnlySpan<byte> data) { }
        public int Read(Span<byte> buffer, int timeoutMs) => 0;
        public void Dispose() { }
    }
}
