using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.LianLiWireless;

namespace Nexus.Service.Tests.LianLiWireless;

public class Slv3LcdHubTests
{
    [Fact]
    public void Screens_starts_empty_and_reflects_the_last_UpdateScreens_call()
    {
        var (hub, _, _) = CreateHub();
        Assert.Empty(hub.Screens);

        hub.UpdateScreens(new[] { new Slv3LcdScreenInfo { Serial = "SER1", Position = 1 } });

        var screen = Assert.Single(hub.Screens);
        Assert.Equal("SER1", screen.Serial);
        Assert.Equal(1, screen.Position);
    }

    [Fact]
    public void Discover_returns_the_discovery_ports()
    {
        var (hub, _, discovery) = CreateHub();
        discovery.Ports.Add(new Slv3LcdPortInfo { PortName = "path-1", Serial = "AAAA111122223333" });

        var ports = hub.Discover();

        var port = Assert.Single(ports);
        Assert.Equal("AAAA111122223333", port.Serial);
    }

    [Fact]
    public void PushImageToSerial_opens_and_pushes()
    {
        var (hub, factory, discovery) = CreateHub();
        discovery.Ports.Add(new Slv3LcdPortInfo { PortName = "path-1", Serial = "SER1" });

        var ok = hub.PushImageToSerial("SER1", new byte[] { 1, 2, 3 });

        Assert.True(ok);
        var transport = Assert.Single(factory.Opened.Values);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(transport.PushedImages));
    }

    [Fact]
    public void PushImageToSerial_reuses_the_cached_transport()
    {
        var (hub, factory, discovery) = CreateHub();
        discovery.Ports.Add(new Slv3LcdPortInfo { PortName = "path-1", Serial = "SER1" });

        Assert.True(hub.PushImageToSerial("SER1", new byte[] { 1 }));
        Assert.True(hub.PushImageToSerial("SER1", new byte[] { 2 }));

        Assert.Equal(1, factory.OpenCallCount);
        var transport = Assert.Single(factory.Opened.Values);
        Assert.Equal(2, transport.PushedImages.Count);
    }

    [Fact]
    public void PushImageToSerial_reopens_after_the_transport_goes_stale()
    {
        var (hub, factory, discovery) = CreateHub();
        discovery.Ports.Add(new Slv3LcdPortInfo { PortName = "path-1", Serial = "SER1" });

        Assert.True(hub.PushImageToSerial("SER1", new byte[] { 1 }));
        factory.Opened["SER1"].SimulateDisconnect();
        Assert.True(hub.PushImageToSerial("SER1", new byte[] { 2 }));

        Assert.Equal(2, factory.OpenCallCount);
    }

    [Fact]
    public void PushImageToSerial_fails_for_unknown_serial()
    {
        var (hub, _, _) = CreateHub();
        Assert.False(hub.PushImageToSerial("NOPE", new byte[] { 1 }));
    }

    [Fact]
    public void SetBrightness_and_SetRotation_reach_the_transport()
    {
        var (hub, factory, discovery) = CreateHub();
        discovery.Ports.Add(new Slv3LcdPortInfo { PortName = "path-1", Serial = "SER1" });

        Assert.True(hub.SetBrightness("SER1", 80));
        Assert.True(hub.SetRotation("SER1", 2));

        var transport = factory.Opened["SER1"];
        Assert.Equal(80, Assert.Single(transport.Brightness));
        Assert.Equal(2, Assert.Single(transport.Rotation));
    }

    [Fact]
    public void TryGetPosition_returns_the_transport_groupIndex()
    {
        var (hub, factory, discovery) = CreateHub();
        discovery.Ports.Add(new Slv3LcdPortInfo { PortName = "path-1", Serial = "SER1" });
        factory.PendingPosition = 2;

        var ok = hub.TryGetPosition("SER1", out var position);

        Assert.True(ok);
        Assert.Equal(2, position);
    }

    [Fact]
    public void TryGetPosition_fails_when_the_transport_does_not_ack()
    {
        var (hub, _, discovery) = CreateHub();
        discovery.Ports.Add(new Slv3LcdPortInfo { PortName = "path-1", Serial = "SER1" });

        var ok = hub.TryGetPosition("SER1", out var position);

        Assert.False(ok);
        Assert.Equal(-1, position);
    }

    [Fact]
    public void PushImageToSerial_returns_false_instead_of_throwing_on_an_oversized_frame()
    {
        var (hub, factory, discovery) = CreateHub();
        discovery.Ports.Add(new Slv3LcdPortInfo { PortName = "path-1", Serial = "SER1" });
        Assert.True(hub.PushImageToSerial("SER1", new byte[] { 1 }));
        factory.Opened["SER1"].ThrowOnPush = true;

        var ok = hub.PushImageToSerial("SER1", new byte[] { 2 });

        Assert.False(ok);
    }

    [Fact]
    public void Dispose_closes_every_open_transport()
    {
        var (hub, factory, discovery) = CreateHub();
        discovery.Ports.Add(new Slv3LcdPortInfo { PortName = "path-1", Serial = "SER1" });
        Assert.True(hub.PushImageToSerial("SER1", new byte[] { 1 }));

        hub.Dispose();

        Assert.True(factory.Opened["SER1"].Disposed);
    }

    private static (Slv3LcdHub Hub, FakeTransportFactory Factory, FakeDiscovery Discovery) CreateHub()
    {
        var discovery = new FakeDiscovery();
        var factory = new FakeTransportFactory();
        var hub = new Slv3LcdHub(discovery, factory.Create);
        return (hub, factory, discovery);
    }

    private sealed class FakeDiscovery : ISlv3LcdDiscovery
    {
        public List<Slv3LcdPortInfo> Ports { get; } = new();
        public IReadOnlyList<Slv3LcdPortInfo> Discover() => Ports;
    }

    private sealed class FakeTransportFactory
    {
        public Dictionary<string, FakeTransport> Opened { get; } = new(StringComparer.Ordinal);
        public int OpenCallCount { get; private set; }
        public byte? PendingPosition { get; set; }

        public ISlv3LcdTransport Create(Slv3LcdPortInfo port)
        {
            OpenCallCount++;
            var transport = new FakeTransport(port.PortName) { PositionToReturn = PendingPosition };
            Opened[port.Serial] = transport;
            return transport;
        }
    }

    private sealed class FakeTransport : ISlv3LcdTransport
    {
        private bool _open = true;

        public FakeTransport(string portName)
        {
            PortName = portName;
        }

        public List<byte[]> PushedImages { get; } = new();
        public List<byte> Brightness { get; } = new();
        public List<byte> Rotation { get; } = new();
        public bool Disposed { get; private set; }
        public byte? PositionToReturn { get; set; }
        public bool ThrowOnPush { get; set; }

        public bool IsOpen => _open;
        public string PortName { get; }

        public void SimulateDisconnect() => _open = false;

        public bool PushImage(byte[] jpeg)
        {
            if (ThrowOnPush)
            {
                throw new ArgumentException("jpeg exceeds the payload capacity");
            }
            PushedImages.Add(jpeg);
            return true;
        }

        public bool SetBrightness(byte value)
        {
            Brightness.Add(value);
            return true;
        }

        public bool SetRotation(byte value)
        {
            Rotation.Add(value);
            return true;
        }

        public bool TryGetPosition(out byte groupIndex)
        {
            groupIndex = PositionToReturn ?? 0;
            return PositionToReturn.HasValue;
        }

        public void Dispose() => Disposed = true;
    }
}
