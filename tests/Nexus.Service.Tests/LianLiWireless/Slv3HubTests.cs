using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.LianLiWireless;
using Xunit;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Tests.LianLiWireless;

/// <summary>
/// Drives Slv3Hub against a fake TX/RX pair that models the RX device-list
/// report as a function of what the TX has been sent, so the bind/unbind
/// state machine converges (or fails to) exactly as it would against real
/// firmware: a bind/unbind frame takes effect on the fake network, and the
/// hub only sees it on its NEXT device-list refresh, matching the two-tick
/// convergence plans/lianli-wireless-support.md section 1.7 describes.
/// </summary>
public class Slv3HubTests
{
    private static readonly byte[] FanMac = Convert.FromHexString("112233445566");

    [Fact]
    public void EnsureConnected_learns_master_mac()
    {
        var (hub, net, _, _) = CreateConnectedHub();
        Assert.True(hub.State.IsConnected);
        Assert.Equal(Convert.ToHexString(net.MasterMac), hub.State.MasterMac);
    }

    [Fact]
    public void DriveTick_surfaces_discovered_fan_as_unbound()
    {
        var (hub, net, _, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac });

        Assert.True(hub.DriveTick());

        var fan = Assert.Single(hub.State.Fans);
        Assert.Equal(Convert.ToHexString(FanMac), fan.Mac);
        Assert.False(fan.BoundToUs);
    }

    [Fact]
    public void Bind_converges_once_device_list_confirms()
    {
        var (hub, net, _, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());

        Assert.True(hub.Bind(Convert.ToHexString(FanMac)));
        Assert.True(hub.DriveTick());
        Assert.False(hub.State.Fans[0].BoundToUs);

        Assert.True(hub.DriveTick());
        Assert.True(hub.State.Fans[0].BoundToUs);
        Assert.Equal(1, hub.State.Fans[0].Slot);
    }

    [Fact]
    public void Unbind_converges_once_device_list_confirms()
    {
        var (hub, net, _, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1 });
        Assert.True(hub.DriveTick());
        Assert.True(hub.State.Fans[0].BoundToUs);

        Assert.True(hub.Unbind(Convert.ToHexString(FanMac)));
        Assert.True(hub.DriveTick());
        Assert.True(hub.DriveTick());

        Assert.False(hub.State.Fans[0].BoundToUs);
        Assert.Equal("", hub.State.Fans[0].MasterMac);
    }

    [Fact]
    public void Bind_on_already_bound_fan_does_not_reassign_slot()
    {
        var (hub, net, _, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 5 });
        Assert.True(hub.DriveTick());
        Assert.Equal(5, hub.State.Fans[0].Slot);

        Assert.True(hub.Bind(Convert.ToHexString(FanMac)));
        Assert.True(hub.DriveTick());
        Assert.True(hub.DriveTick());

        Assert.Equal(5, hub.State.Fans[0].Slot);
    }

    [Fact]
    public void Bind_rejects_mac_never_seen_in_device_list()
    {
        var (hub, _, _, _) = CreateConnectedHub();
        Assert.False(hub.Bind(Convert.ToHexString(FanMac)));
    }

    [Fact]
    public void Identify_sends_rf_select_frame_addressed_to_the_fan()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 2 });
        Assert.True(hub.DriveTick());

        Assert.True(hub.Identify(Convert.ToHexString(FanMac)));

        var selectFrame = Assert.Single(tx.SentFrames, f => f.Length >= 12 && f[5] == Slv3Protocol.RfSelect);
        Assert.Equal(FanMac, selectFrame.AsSpan(6, 6).ToArray());
    }

    [Fact]
    public void SendRgbFrame_sends_header_four_times_then_data_parts()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 3 });
        Assert.True(hub.DriveTick());

        var leds = new RgbColor[40];
        for (var i = 0; i < leds.Length; i++)
        {
            leds[i] = new RgbColor((byte)i, (byte)(i * 2), (byte)(i * 3));
        }

        var sent = hub.SendRgbFrame(Convert.ToHexString(FanMac), leds, 100, 100, out var effectIndexHex);

        Assert.True(sent);
        Assert.Equal(8, effectIndexHex.Length);

        var rgbFrames = tx.SentFrames.FindAll(f => f.Length >= 6 && f[1] == 0 && f[4] == Slv3Protocol.RfFrameType && f[5] == Slv3Protocol.RfRgbSync);
        // Header packet (part 0) is sent 4 times; at least one more data part follows.
        Assert.True(rgbFrames.Count >= 5, $"expected at least 5 chunk-0 RF_RgbSync frames, got {rgbFrames.Count}");
        foreach (var frame in rgbFrames)
        {
            Assert.Equal(FanMac, frame.AsSpan(6, 6).ToArray());
        }
    }

    [Fact]
    public void SendRgbFrame_fails_for_unbound_fan()
    {
        var (hub, net, _, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());

        var sent = hub.SendRgbFrame(Convert.ToHexString(FanMac), new RgbColor[40], 100, 100, out var effectIndexHex);

        Assert.False(sent);
        Assert.Equal("", effectIndexHex);
    }

    [Fact]
    public void SendRgbFrame_fails_for_unknown_mac()
    {
        var (hub, _, _, _) = CreateConnectedHub();
        var sent = hub.SendRgbFrame(Convert.ToHexString(FanMac), new RgbColor[40], 100, 100, out var effectIndexHex);
        Assert.False(sent);
        Assert.Equal("", effectIndexHex);
    }

    [Fact]
    public void SetChannel_rejects_even_non_default_channel()
    {
        var (hub, _, _, _) = CreateConnectedHub();
        Assert.False(hub.SetChannel(10));
    }

    [Fact]
    public void SetChannel_accepts_default_and_odd_values()
    {
        var (hub, _, _, _) = CreateConnectedHub();
        Assert.True(hub.SetChannel(Slv3Protocol.DefaultChannel));
        Assert.True(hub.SetChannel(15));
        Assert.Equal(15, hub.State.Channel);
    }

    // ── SetPortDuty / bind-frame PWM tuple (Phase 3) ──

    [Fact]
    public void DriveTick_defaults_every_occupied_port_to_mobo_sync()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, FanCount = 3 });

        Assert.True(hub.DriveTick());

        var bind = LastBindFrame(tx, FanMac);
        Assert.Equal(Slv3Protocol.PwmFollowMotherboard, bind[21]);
        Assert.Equal(Slv3Protocol.PwmFollowMotherboard, bind[22]);
        Assert.Equal(Slv3Protocol.PwmFollowMotherboard, bind[23]);
        Assert.Equal(0, bind[24]); // port 3 is beyond FanCount=3: unoccupied
    }

    [Fact]
    public void SetPortDuty_writes_a_floored_manual_duty_into_the_next_bind_frame()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, FanCount = 2 });
        Assert.True(hub.DriveTick());

        Assert.True(hub.SetPortDuty(Convert.ToHexString(FanMac), 0, 50));
        Assert.True(hub.SetPortDuty(Convert.ToHexString(FanMac), 1, 5)); // floors to 14

        Assert.True(hub.DriveTick());

        var bind = LastBindFrame(tx, FanMac);
        Assert.Equal(50, bind[21]);
        Assert.Equal(14, bind[22]);
        Assert.Equal(0, bind[23]); // unoccupied port stays 0 even if never set
        Assert.Equal(0, bind[24]);
    }

    [Fact]
    public void SetPortDuty_null_restores_mobo_sync()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, FanCount = 1 });
        Assert.True(hub.DriveTick());
        Assert.True(hub.SetPortDuty(Convert.ToHexString(FanMac), 0, 80));
        Assert.True(hub.DriveTick());
        Assert.Equal(80, LastBindFrame(tx, FanMac)[21]);

        Assert.True(hub.SetPortDuty(Convert.ToHexString(FanMac), 0, null));
        Assert.True(hub.DriveTick());

        Assert.Equal(Slv3Protocol.PwmFollowMotherboard, LastBindFrame(tx, FanMac)[21]);
    }

    [Fact]
    public void SetPortDuty_rejects_out_of_range_port_and_malformed_mac()
    {
        var (hub, _, _, _) = CreateConnectedHub();
        Assert.False(hub.SetPortDuty(Convert.ToHexString(FanMac), -1, 50));
        Assert.False(hub.SetPortDuty(Convert.ToHexString(FanMac), 4, 50));
        Assert.False(hub.SetPortDuty("not-a-mac", 0, 50));
    }

    // Finds the most recent RF_Bind USB frame (chunk 0) addressed to fanMac,
    // whose bytes [21..25) carry the 4-port PWM tuple (RF-payload [17..21),
    // shifted by the 4-byte USB-frame header).
    private static byte[] LastBindFrame(FakeTxTransport tx, byte[] fanMac)
    {
        byte[]? found = null;
        foreach (var frame in tx.SentFrames)
        {
            if (frame.Length < 25 || frame[0] != Slv3Protocol.UsbSendRf || frame[1] != 0
                || frame[4] != Slv3Protocol.RfFrameType || frame[5] != Slv3Protocol.RfBind)
            {
                continue;
            }
            if (!Slv3Protocol.MacEquals(frame.AsSpan(6, 6), fanMac)) continue;
            found = frame;
        }
        Assert.NotNull(found);
        return found!;
    }

    private static (Slv3Hub Hub, FakeSlv3Network Net, FakeTxTransport Tx, FakeRxTransport Rx) CreateConnectedHub()
    {
        var net = new FakeSlv3Network();
        var tx = new FakeTxTransport(net);
        var rx = new FakeRxTransport(net);
        var hub = new Slv3Hub(new FakeDiscovery(), port => port.Role == Slv3DongleRole.Tx ? tx : rx);
        Assert.True(hub.EnsureConnected());
        return (hub, net, tx, rx);
    }

    private sealed class FakeDiscovery : ISlv3Discovery
    {
        public IReadOnlyList<Slv3PortInfo> Discover() => new[]
        {
            new Slv3PortInfo { PortName = "fake-tx", Role = Slv3DongleRole.Tx },
            new Slv3PortInfo { PortName = "fake-rx", Role = Slv3DongleRole.Rx },
        };
    }

    private sealed class SimulatedFan
    {
        public required byte[] Mac { get; init; }
        public byte[] MasterMac { get; set; } = new byte[6];
        public byte Channel { get; set; } = Slv3Protocol.DefaultChannel;
        public byte RxType { get; set; }
        public byte DevType { get; set; } = 25;
        public byte FanCount { get; set; } = 1;
    }

    private sealed class FakeSlv3Network
    {
        public byte[] MasterMac { get; } = Convert.FromHexString("AABBCCDDEEFF");
        public List<SimulatedFan> Fans { get; } = new();
    }

    // Models the TX dongle: records every USB frame sent, and applies an
    // RF_Bind frame's target slot/master/channel to the addressed fan
    // immediately (no RF latency simulated), so the next GetDev report
    // reflects it.
    private sealed class FakeTxTransport : ISlv3Transport
    {
        private readonly FakeSlv3Network _net;

        public FakeTxTransport(FakeSlv3Network net)
        {
            _net = net;
        }

        public bool IsOpen => true;
        public Slv3DongleRole Role => Slv3DongleRole.Tx;
        public string PortName => "fake-tx";
        public List<byte[]> SentFrames { get; } = new();

        public bool RfSend(ReadOnlySpan<byte> frame)
        {
            var copy = frame.ToArray();
            SentFrames.Add(copy);
            if (copy.Length >= 21 && copy[0] == Slv3Protocol.UsbSendRf && copy[1] == 0 && copy[5] == Slv3Protocol.RfBind)
            {
                var fanMac = copy.AsSpan(6, 6).ToArray();
                var masterMac = copy.AsSpan(12, 6).ToArray();
                var targetChannel = copy[19];
                var slot = copy[20];
                foreach (var fan in _net.Fans)
                {
                    if (!Slv3Protocol.MacEquals(fan.Mac, fanMac))
                    {
                        continue;
                    }
                    fan.RxType = slot;
                    fan.Channel = targetChannel;
                    fan.MasterMac = slot == 0 ? new byte[6] : masterMac;
                    break;
                }
            }
            return true;
        }

        public byte[] RfRead(int expectedLen)
        {
            var reply = new byte[64];
            reply[0] = Slv3Protocol.UsbGetMac;
            _net.MasterMac.CopyTo(reply, 1);
            return reply;
        }

        public void Dispose()
        {
        }
    }

    // Models the RX dongle: GetDev replies are generated live from the fake
    // network's current fan states, so a bind/unbind the TX fake just applied
    // is visible on the following RfRead.
    private sealed class FakeRxTransport : ISlv3Transport
    {
        private readonly FakeSlv3Network _net;

        public FakeRxTransport(FakeSlv3Network net)
        {
            _net = net;
        }

        public bool IsOpen => true;
        public Slv3DongleRole Role => Slv3DongleRole.Rx;
        public string PortName => "fake-rx";

        public bool RfSend(ReadOnlySpan<byte> frame) => true;

        public byte[] RfRead(int expectedLen)
        {
            var buf = new byte[Slv3Protocol.RecordHeaderLength + _net.Fans.Count * Slv3Protocol.RecordLength];
            buf[0] = Slv3Protocol.UsbSendRf;
            buf[1] = (byte)_net.Fans.Count;
            for (var i = 0; i < _net.Fans.Count; i++)
            {
                var offset = Slv3Protocol.RecordHeaderLength + i * Slv3Protocol.RecordLength;
                var rec = buf.AsSpan(offset);
                var fan = _net.Fans[i];
                fan.Mac.CopyTo(rec);
                fan.MasterMac.CopyTo(rec.Slice(6));
                rec[12] = fan.Channel;
                rec[13] = fan.RxType;
                rec[18] = fan.DevType;
                rec[19] = fan.FanCount;
                rec[41] = Slv3Protocol.RecordValidator;
            }
            return buf;
        }

        public void Dispose()
        {
        }
    }
}
