using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.LianLiWireless;

namespace Nexus.Service.Tests.LianLiWireless;

public class Slv3LcdHubTests
{
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

        public ISlv3LcdTransport Create(Slv3LcdPortInfo port)
        {
            OpenCallCount++;
            var transport = new FakeTransport(port.PortName);
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

        public bool IsOpen => _open;
        public string PortName { get; }

        public void SimulateDisconnect() => _open = false;

        public bool PushImage(byte[] jpeg)
        {
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

        public void Dispose() => Disposed = true;
    }
}
