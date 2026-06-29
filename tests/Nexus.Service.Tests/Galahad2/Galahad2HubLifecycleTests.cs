using Nexus.Service.Peripherals.Galahad2;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.LianLiCp;

namespace Nexus.Service.Tests.Galahad2;

// Minimal fake: records Write calls, returns a configurable reply on Read.
internal sealed class Galahad2DeviceFake : IHidDevice
{
    private readonly byte[]? _connectReply;

    public List<byte[]> Writes { get; } = new();

    public Galahad2DeviceFake(byte[]? connectReply = null)
    {
        _connectReply = connectReply;
    }

    public int VendorId => Galahad2Protocol.VendorId;
    public int ProductId => Galahad2Protocol.ProductIdPerformance;
    public string Path => "fake";
    public string? Serial => null;
    public int UsagePage => 0xFF00;
    public int Usage => 0x01;

    public bool Write(ReadOnlySpan<byte> report)
    {
        Writes.Add(report.ToArray());
        return true;
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        if (_connectReply == null)
        {
            return 0;
        }
        int n = Math.Min(buffer.Length, _connectReply.Length);
        _connectReply.AsSpan(0, n).CopyTo(buffer);
        return n;
    }

    public bool SetFeature(ReadOnlySpan<byte> report) => false;
    public bool GetFeature(Span<byte> buffer) => false;
    public bool GetInputReport(Span<byte> buffer) => false;
    public bool SetOutputReport(ReadOnlySpan<byte> report) => false;
    public void Dispose() { }
}

public class Galahad2HubLifecycleTests
{
    // Builds a 64-byte handshake reply with the given RPMs (BE16 each).
    private static byte[] BuildHandshakeReply(int fanRpm, int pumpRpm)
    {
        var packet = new byte[CommandPacket.Length];
        packet[0] = CommandPacket.ReportId;
        packet[1] = 0x81;
        packet[5] = 4;
        int offset = CommandPacket.PayloadOffset;
        packet[offset] = (byte)(fanRpm >> 8);
        packet[offset + 1] = (byte)(fanRpm & 0xFF);
        packet[offset + 2] = (byte)(pumpRpm >> 8);
        packet[offset + 3] = (byte)(pumpRpm & 0xFF);
        return packet;
    }

    [Fact]
    public void Connect_returns_true_when_device_replies()
    {
        var fake = new Galahad2DeviceFake(BuildHandshakeReply(1200, 2400));
        using var hub = new Galahad2Hub();
        hub.Attach(fake);

        bool ok = hub.Connect();

        Assert.True(ok);
        Assert.True(hub.IsConnected);
    }

    [Fact]
    public void Connect_returns_false_when_device_does_not_reply()
    {
        var fake = new Galahad2DeviceFake(connectReply: null);
        using var hub = new Galahad2Hub();
        hub.Attach(fake);

        bool ok = hub.Connect();

        Assert.False(ok);
        Assert.False(hub.IsConnected);
    }

    [Fact]
    public void Detach_resets_IsConnected_and_snapshot_to_empty()
    {
        var fake = new Galahad2DeviceFake(BuildHandshakeReply(1000, 1500));
        using var hub = new Galahad2Hub();
        hub.Attach(fake);
        hub.Connect();
        Assert.True(hub.IsConnected);

        hub.Detach();

        Assert.False(hub.IsConnected);
        Assert.Same(Galahad2Snapshot.Empty, hub.Snapshot);
    }

    [Fact]
    public void Snapshot_taken_before_Detach_is_unaffected_by_Detach()
    {
        var fake = new Galahad2DeviceFake(BuildHandshakeReply(0, 0));
        using var hub = new Galahad2Hub();
        hub.Attach(fake);
        hub.Connect();
        hub.SetFan(75);

        var snapBefore = hub.Snapshot;
        Assert.Equal(75, snapBefore.FanDuty);

        hub.Detach();

        // Hub snapshot is now Empty; the snapshot we captured before is unchanged.
        Assert.Equal(75, snapBefore.FanDuty);
        Assert.Equal(0, hub.Snapshot.FanDuty);
    }

    [Fact]
    public void SetFan_updates_FanDuty_in_snapshot()
    {
        var fake = new Galahad2DeviceFake(BuildHandshakeReply(0, 0));
        using var hub = new Galahad2Hub();
        hub.Attach(fake);
        hub.Connect();

        hub.SetFan(80);

        Assert.Equal(80, hub.Snapshot.FanDuty);
    }

    [Fact]
    public void SetPump_updates_PumpDuty_in_snapshot()
    {
        var fake = new Galahad2DeviceFake(BuildHandshakeReply(0, 0));
        using var hub = new Galahad2Hub();
        hub.Attach(fake);
        hub.Connect();

        hub.SetPump(70);

        Assert.Equal(70, hub.Snapshot.PumpDuty);
    }

    [Fact]
    public void PollRpm_updates_FanRpm_and_PumpRpm_in_snapshot()
    {
        // Fake replies with the handshake reply on every Read.
        var fake = new Galahad2DeviceFake(BuildHandshakeReply(fanRpm: 1300, pumpRpm: 2100));
        using var hub = new Galahad2Hub();
        hub.Attach(fake);
        hub.Connect();

        bool ok = hub.PollRpm();

        Assert.True(ok);
        Assert.Equal(1300, hub.Snapshot.FanRpm);
        Assert.Equal(2100, hub.Snapshot.PumpRpm);
    }

    [Fact]
    public void Connect_initialises_PumpDuty_to_PumpDutyFloor()
    {
        var fake = new Galahad2DeviceFake(BuildHandshakeReply(0, 0));
        using var hub = new Galahad2Hub();
        hub.Attach(fake);
        hub.Connect();

        Assert.Equal(Galahad2Protocol.PumpDutyFloor, hub.Snapshot.PumpDuty);
    }
}
