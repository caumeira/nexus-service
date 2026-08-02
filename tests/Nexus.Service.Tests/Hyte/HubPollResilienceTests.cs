using System;
using System.Collections.Generic;
using System.IO;
using Nexus.Service.Peripherals.Hyte;
using Nexus.Service.Peripherals.Hyte.MiniHub;
using Nexus.Service.Peripherals.Hyte.Np50;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Tests.Hyte;

/// <summary>
/// A desynced poll reply must not drop a transport the 30 Hz lighting writer
/// shares. Reproduces the reported NP50 symptom: one bad GetChannelInfo header
/// tore the port down, and re-discovery took tens of seconds with the LEDs dark.
/// </summary>
public class HubPollResilienceTests
{
    // FF CC + zeroes: a well-formed 20-byte hub-info reply.
    private static byte[] GoodHubInfo()
    {
        var reply = new byte[20];
        reply[0] = 0xFF;
        reply[1] = 0xCC;
        return reply;
    }

    // The observed failure: 20 bytes of zeroes, so ExpectHeader rejects [0x00 0x00].
    private static byte[] DesyncedReply() => new byte[20];

    [Fact]
    public void Np50_tolerates_a_desynced_reply_without_dropping_the_port()
    {
        var hub = NewNp50(out var transports);

        Assert.False(hub.PollHubInfo());

        Assert.True(hub.IsConnected);
        Assert.Single(transports);
        Assert.False(transports[0].Disposed);
    }

    [Fact]
    public void Np50_keeps_streaming_lighting_while_a_desync_is_tolerated()
    {
        var hub = NewNp50(out var transports);
        hub.PollHubInfo();
        var writesAfterPoll = transports[0].Writes.Count;

        hub.WriteLighting(1, new[] { new RgbColor(0x10, 0x20, 0x30) });

        Assert.True(transports[0].Writes.Count > writesAfterPoll);
        Assert.False(transports[0].Disposed);
    }

    [Fact]
    public void Np50_drops_the_port_once_the_desync_is_sustained()
    {
        var hub = NewNp50(out var transports);

        for (var i = 0; i < PollFailureTracker.Threshold; i++)
        {
            Assert.False(hub.PollHubInfo());
        }

        Assert.False(hub.IsConnected);
        Assert.True(transports[0].Disposed);
    }

    [Fact]
    public void Np50_success_between_desyncs_keeps_the_port_open_indefinitely()
    {
        var hub = NewNp50(out var transports);
        var reply = DesyncedReply();
        transports[0].NextRead = () => reply;

        for (var round = 0; round < 10; round++)
        {
            for (var i = 1; i < PollFailureTracker.Threshold; i++)
            {
                reply = DesyncedReply();
                hub.PollHubInfo();
            }
            reply = GoodHubInfo();
            Assert.True(hub.PollHubInfo());
        }

        Assert.True(hub.IsConnected);
        Assert.Single(transports);
    }

    /// <summary>
    /// A request that never reached the hub leaves its software-control
    /// watchdog unfed, and the poll cadence is deliberately half of
    /// HeartbeatRevertMs - so retrying would spend the whole revert budget.
    /// </summary>
    [Fact]
    public void Np50_drops_the_port_immediately_when_the_request_cannot_be_sent()
    {
        var hub = NewNp50(out var transports);
        transports[0].FailWrite = true;

        Assert.False(hub.PollHubInfo());

        Assert.False(hub.IsConnected);
        Assert.True(transports[0].Disposed);
    }

    [Fact]
    public void Np50_ignores_a_transport_disposed_by_another_thread()
    {
        var hub = NewNp50(out var transports);
        transports[0].ThrowDisposedOnRead = true;

        for (var i = 0; i < PollFailureTracker.Threshold * 2; i++)
        {
            Assert.False(hub.PollHubInfo());
        }

        // Swallowed, not counted: the teardown was someone else's, so it must
        // not consume this hub's desync budget.
        Assert.Single(transports);
    }

    [Fact]
    public void MiniHub_tolerates_a_short_read_then_drops_at_the_threshold()
    {
        var transports = new List<FakeTransport>();
        var hub = new MiniHubHub(
            new FakeDiscovery(),
            _ => Track(transports, () => Array.Empty<byte>()));

        Assert.False(hub.PollFanSpeeds());
        Assert.True(hub.IsConnected);

        for (var i = 1; i < PollFailureTracker.Threshold; i++)
        {
            hub.PollFanSpeeds();
        }

        Assert.False(hub.IsConnected);
        Assert.True(transports[0].Disposed);
    }

    private static Np50Hub NewNp50(out List<FakeTransport> transports)
    {
        var list = new List<FakeTransport>();
        transports = list;
        var hub = new Np50Hub(new FakeDiscovery(), _ => Track(list, DesyncedReply));
        hub.EnsureConnected();
        return hub;
    }

    private static FakeTransport Track(List<FakeTransport> sink, Func<byte[]> read)
    {
        var t = new FakeTransport { NextRead = read };
        sink.Add(t);
        return t;
    }

    private sealed class FakeDiscovery : INp50PortDiscovery
    {
        public IReadOnlyList<Np50PortInfo> Discover()
            => new[] { new Np50PortInfo { PortName = "COM_TEST", Serial = "NPTEST" } };
    }

    private sealed class FakeTransport : INp50Transport
    {
        public readonly List<byte[]> Writes = new();
        public Func<byte[]> NextRead = Array.Empty<byte>;
        public bool FailWrite;
        public bool ThrowDisposedOnRead;
        public bool Disposed { get; private set; }

        public bool IsOpen => !Disposed;
        public string Serial => "NPTEST";
        public void DiscardInput() { }

        public void Write(ReadOnlySpan<byte> data)
        {
            if (FailWrite) throw new IOException("port write failed");
            Writes.Add(data.ToArray());
        }

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            if (ThrowDisposedOnRead) throw new ObjectDisposedException(nameof(FakeTransport));
            var src = NextRead();
            var n = Math.Min(src.Length, buffer.Length);
            src.AsSpan(0, n).CopyTo(buffer);
            return n;
        }

        public void Dispose() => Disposed = true;
    }
}
