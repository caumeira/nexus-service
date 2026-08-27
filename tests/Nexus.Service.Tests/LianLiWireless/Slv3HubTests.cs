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

    // Mirrors Slv3Hub's private PendingOpTickBudget: a bind/unbind not
    // confirmed within this many ticks is dropped as non-converging.
    private const int PendingOpTickBudget = 15;

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
    public void DriveTick_surfaces_more_than_one_page_of_fans()
    {
        var (hub, net, _, _) = CreateConnectedHub();
        // 12 bound chains > one 10-record device-list page. A single-page poll
        // truncates the overflow, so those chains vanish from the list and can't
        // be seen, paired, or confirm a bind (the "many sets" report). The poll
        // must request a second page.
        const int fanCount = 12;
        for (var i = 0; i < fanCount; i++)
        {
            var mac = new byte[6];
            mac[5] = (byte)(0x10 + i);
            net.Fans.Add(new SimulatedFan { Mac = mac, MasterMac = net.MasterMac, RxType = (byte)(i + 1) });
        }

        // First poll seeds pageCount from a zero count (one page, so it still sees
        // only 10); the reported count then tunes the next poll up to two pages.
        Assert.True(hub.DriveTick());
        Assert.True(hub.DriveTick());

        Assert.Equal(fanCount, hub.State.Fans.Length);
        Assert.All(hub.State.Fans, f => Assert.True(f.BoundToUs));
    }

    [Fact]
    public void Transient_empty_poll_keeps_a_multi_page_fan_list_whole()
    {
        var (hub, net, _, _) = CreateConnectedHub();
        const int fanCount = 12;
        for (var i = 0; i < fanCount; i++)
        {
            var mac = new byte[6];
            mac[5] = (byte)(0x10 + i);
            net.Fans.Add(new SimulatedFan { Mac = mac, MasterMac = net.MasterMac, RxType = (byte)(i + 1) });
        }
        Assert.True(hub.DriveTick());
        Assert.True(hub.DriveTick());
        Assert.Equal(fanCount, hub.State.Fans.Length);

        // A transient empty poll (RF hiccup) is debounced - it must not reset the
        // learned page count, or the recovery poll would request one page and
        // re-truncate the list back to 10.
        var saved = net.Fans.ToArray();
        net.Fans.Clear();
        Assert.True(hub.DriveTick());
        Assert.Equal(fanCount, hub.State.Fans.Length);   // debounce holds the list

        net.Fans.AddRange(saved);
        Assert.True(hub.DriveTick());
        Assert.Equal(fanCount, hub.State.Fans.Length);   // recovery poll stays 2 pages
    }

    [Fact]
    public void Bind_converges_on_the_first_tick_via_the_immediate_frame()
    {
        var (hub, net, _, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());

        // Bind() sends the first bind frame itself; the next poll confirms.
        Assert.True(hub.Bind(Convert.ToHexString(FanMac)));
        Assert.True(hub.DriveTick());
        Assert.True(hub.State.Fans[0].BoundToUs);
        Assert.Equal(1, hub.State.Fans[0].Slot);
    }

    [Fact]
    public void Bind_converges_via_tick_resend_when_the_immediate_frame_is_lost()
    {
        var (hub, net, _, _) = CreateConnectedHub();
        var fan = new SimulatedFan { Mac = FanMac };
        net.Fans.Add(fan);
        Assert.True(hub.DriveTick());

        // Fan drops off the RF network (beacon starved): the merged device list
        // still carries it, so Bind() is accepted, but the immediate frame is
        // lost (nothing on the fake network to apply it to).
        net.Fans.Clear();
        Assert.True(hub.Bind(Convert.ToHexString(FanMac)));
        Assert.True(hub.DriveTick());
        Assert.False(hub.State.Fans[0].BoundToUs);

        // Fan reappears; the pending op's per-tick re-send binds it.
        net.Fans.Add(fan);
        Assert.True(hub.DriveTick());
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
    public void ResetChain_sends_reboot_frames_addressed_to_the_fan()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 2 });
        Assert.True(hub.DriveTick());
        tx.SentFrames.Clear();

        Assert.True(hub.ResetChain(Convert.ToHexString(FanMac)));

        var rebootFrames = tx.SentFrames.FindAll(f =>
            f.Length >= 12 && f[1] == 0 && f[4] == Slv3Protocol.RfFrameType && f[5] == Slv3Protocol.RfRebootChain);
        Assert.Equal(3, rebootFrames.Count);
        Assert.All(rebootFrames, f => Assert.Equal(FanMac, f.AsSpan(6, 6).ToArray()));
    }

    [Fact]
    public void ResetChain_fails_for_unknown_mac()
    {
        var (hub, _, _, _) = CreateConnectedHub();
        Assert.False(hub.ResetChain(Convert.ToHexString(FanMac)));
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

        var sent = hub.SendRgbFrame(Convert.ToHexString(FanMac), leds, 100, 100, streaming: false, out var effectIndexHex);

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
    public void SendRgbFrame_streaming_tier_sends_header_twice()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 3 });
        Assert.True(hub.DriveTick());
        tx.SentFrames.Clear();

        var sent = hub.SendRgbFrame(Convert.ToHexString(FanMac), new RgbColor[40], 100, 100, streaming: true, out _);

        Assert.True(sent);
        // Part-0 payloads appear once per header repeat; chunk seq 0 of each
        // payload carries packetIndex at RF byte [18] = frame byte [22].
        var headerFrames = tx.SentFrames.FindAll(f =>
            f.Length >= 23 && f[1] == 0 && f[4] == Slv3Protocol.RfFrameType && f[5] == Slv3Protocol.RfRgbSync && f[22] == 0);
        Assert.Equal(2, headerFrames.Count);
    }

    [Fact]
    public void SendRgbFrame_fails_for_unbound_fan()
    {
        var (hub, net, _, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());

        var sent = hub.SendRgbFrame(Convert.ToHexString(FanMac), new RgbColor[40], 100, 100, streaming: false, out var effectIndexHex);

        Assert.False(sent);
        Assert.Equal("", effectIndexHex);
    }

    [Fact]
    public void SendRgbFrame_fails_for_unknown_mac()
    {
        var (hub, _, _, _) = CreateConnectedHub();
        var sent = hub.SendRgbFrame(Convert.ToHexString(FanMac), new RgbColor[40], 100, 100, streaming: false, out var effectIndexHex);
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

    // ── Chain persistence / device-list merge (link-health) ──

    [Fact]
    public void Chain_persists_through_empty_polls_until_expiry()
    {
        var clock = new ManualClock();
        var (hub, net, _, _) = CreateConnectedHub(clock.NowMs);
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());
        Assert.Single(hub.State.Fans);

        // Chain drops off the RF network but the poll keeps succeeding
        // (empty reply); a merge-based list must not evict it immediately.
        net.Fans.Clear();
        for (var i = 0; i < 5; i++)
        {
            Assert.True(hub.DriveTick());
        }
        var fan = Assert.Single(hub.State.Fans);
        Assert.False(fan.Stale);

        clock.AdvanceMs(4_000); // past ChainStaleMs (3500 ms)
        Assert.True(hub.DriveTick());
        fan = Assert.Single(hub.State.Fans);
        Assert.True(fan.Stale);

        clock.AdvanceMs(31_000); // cumulative unseen time now past ChainExpiryMs (30000 ms)
        Assert.True(hub.DriveTick());
        Assert.Empty(hub.State.Fans);
    }

    [Fact]
    public void Chain_seen_every_poll_never_goes_stale_despite_slow_clock_advance()
    {
        var clock = new ManualClock();
        var (hub, net, _, _) = CreateConnectedHub(clock.NowMs);
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());
        Assert.False(hub.State.Fans[0].Stale);

        clock.AdvanceMs(4_000); // would exceed ChainStaleMs if unseen, but the fan is re-reported below
        Assert.True(hub.DriveTick());

        Assert.False(hub.State.Fans[0].Stale);
    }

    // ── GetDev failure escalation / RX reset ──

    [Fact]
    public void Five_consecutive_getdev_failures_reset_the_rx_and_keep_the_list()
    {
        var (hub, net, _, rx) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());

        // A healthy handle that keeps returning an unreadable reply (wedged
        // RX MCU) drives the 5-consecutive-failure reset path.
        rx.FailReads = true;
        for (var i = 0; i < 5; i++)
        {
            Assert.True(hub.DriveTick());
        }
        rx.FailReads = false;

        var resetFrame = Assert.Single(rx.SentFrames, f => f.Length >= 1 && f[0] == Slv3Protocol.UsbResetAnother);
        Assert.Equal(0x15, resetFrame[0]);

        Assert.True(hub.DriveTick());
        Assert.Single(hub.State.Fans);
    }

    [Fact]
    public void Getdev_failures_past_the_reset_budget_make_drivetick_fail()
    {
        var (hub, net, _, rx) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());

        rx.FailReads = true;
        // 3 resets x a 5-fail streak each = 15 ticks; a 4th streak of 5 fails
        // then exhausts MaxRxResetsPerConnection and DriveTick starts failing.
        var results = new bool[20];
        for (var i = 0; i < results.Length; i++)
        {
            results[i] = hub.DriveTick();
        }

        Assert.True(results[18]);
        Assert.False(results[19]);
        Assert.Equal(3, rx.SentFrames.FindAll(f => f.Length >= 1 && f[0] == Slv3Protocol.UsbResetAnother).Count);
    }

    [Fact]
    public void Getdev_send_failure_fails_the_tick_immediately_without_a_reset()
    {
        var (hub, net, _, rx) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());

        // A dead USB handle (the write itself fails) is fatal for the tick;
        // it must not be folded into the wedged-MCU reset streak.
        rx.FailSend = true;
        Assert.False(hub.DriveTick());

        Assert.DoesNotContain(rx.SentFrames, f => f.Length >= 1 && f[0] == Slv3Protocol.UsbResetAnother);
    }

    // ── Channel scan (MasterInitLocked) ──

    [Fact]
    public void EnsureConnected_scans_channels_when_the_master_answers_off_default()
    {
        var net = new FakeSlv3Network();
        var tx = new FakeTxTransport(net) { MasterChannel = 15 };
        var rx = new FakeRxTransport(net);
        var hub = new Slv3Hub(new FakeDiscovery(), port => port.Role == Slv3DongleRole.Tx ? tx : rx);

        // One attempt probes a bounded slice of the scan order (each dead
        // channel costs a full read timeout under the hub lock); the cursor
        // resumes across attempts, mirroring the worker's connect retries.
        var connected = false;
        for (var attempt = 0; attempt < 6 && !connected; attempt++)
        {
            connected = hub.EnsureConnected();
        }

        Assert.True(connected);
        Assert.Equal(15, hub.State.Channel);
    }

    [Fact]
    public void EnsureConnected_skips_the_scan_when_the_master_answers_on_the_default_channel()
    {
        var net = new FakeSlv3Network();
        var tx = new FakeTxTransport(net); // MasterChannel defaults to Slv3Protocol.DefaultChannel
        var rx = new FakeRxTransport(net);
        var hub = new Slv3Hub(new FakeDiscovery(), port => port.Role == Slv3DongleRole.Tx ? tx : rx);

        Assert.True(hub.EnsureConnected());

        Assert.Equal(Slv3Protocol.DefaultChannel, hub.State.Channel);
        Assert.Single(tx.SentFrames, f => f.Length >= 2 && f[0] == Slv3Protocol.UsbGetMac);
    }

    // ── RF_SaveCfg after a confirmed bind/unbind ──

    [Fact]
    public void Confirmed_bind_broadcasts_savecfg_exactly_three_times()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());

        Assert.True(hub.Bind(Convert.ToHexString(FanMac)));
        tx.SentFrames.Clear();

        Assert.True(hub.DriveTick());
        Assert.True(hub.DriveTick());
        Assert.True(hub.DriveTick());
        Assert.True(hub.DriveTick());

        var saveCfgFrames = tx.SentFrames.FindAll(f =>
            f.Length >= 18 && f[0] == Slv3Protocol.UsbSendRf && f[1] == 0
            && f[4] == Slv3Protocol.RfFrameType && f[5] == Slv3Protocol.RfSaveCfg);

        Assert.Equal(3, saveCfgFrames.Count);
        foreach (var frame in saveCfgFrames)
        {
            Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, frame.AsSpan(6, 6).ToArray());
            Assert.Equal(net.MasterMac, frame.AsSpan(12, 6).ToArray());
        }
    }

    // ── Pending bind/unbind drop after PendingOpTickBudget ──

    [Fact]
    public void Pending_bind_drops_after_the_tick_budget_and_does_not_resume_on_its_own()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        var fan = new SimulatedFan { Mac = FanMac };
        net.Fans.Add(fan);
        Assert.True(hub.DriveTick());

        net.Fans.Clear(); // fan unreachable: neither the immediate nor the re-send frame ever applies
        Assert.True(hub.Bind(Convert.ToHexString(FanMac)));

        for (var i = 0; i < PendingOpTickBudget + 1; i++)
        {
            Assert.True(hub.DriveTick());
        }

        tx.SentFrames.Clear();
        net.Fans.Add(fan);
        Assert.True(hub.DriveTick());
        Assert.True(hub.DriveTick());

        Assert.False(hub.State.Fans[0].BoundToUs);
        Assert.DoesNotContain(tx.SentFrames, f =>
            f.Length >= 12 && f[0] == Slv3Protocol.UsbSendRf && f[1] == 0
            && f[4] == Slv3Protocol.RfFrameType && f[5] == Slv3Protocol.RfBind
            && Slv3Protocol.MacEquals(f.AsSpan(6, 6), FanMac));
    }

    // ── EnsureVideoMode ──

    [Fact]
    public void EnsureVideoMode_arms_once_then_rearms_only_after_reconnect()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());
        tx.SentFrames.Clear();

        Assert.True(hub.EnsureVideoMode());

        Assert.Equal(2, tx.SentFrames.Count); // 1 video-start + 1 prep frame (one known chain)
        Assert.Equal(Slv3Protocol.UsbGetMac, tx.SentFrames[0][0]);
        Assert.Equal(0x01, tx.SentFrames[0][1]);
        Assert.Equal(Slv3Protocol.UsbSendRf, tx.SentFrames[1][0]);
        Assert.Equal(0xFF, tx.SentFrames[1][3]);

        tx.SentFrames.Clear();
        Assert.True(hub.EnsureVideoMode()); // idempotent until disconnect
        Assert.Empty(tx.SentFrames);

        hub.Disconnect();
        Assert.True(hub.EnsureConnected());
        tx.SentFrames.Clear();

        Assert.True(hub.EnsureVideoMode());
        Assert.NotEmpty(tx.SentFrames.FindAll(f => f.Length >= 2 && f[0] == Slv3Protocol.UsbGetMac && f[1] == 0x01));
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

    [Fact]
    public void DriveTick_stops_re_binding_a_converged_chain()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());
        Assert.True(hub.Bind(Convert.ToHexString(FanMac)));
        Assert.True(hub.DriveTick());
        Assert.True(hub.State.Fans[0].BoundToUs);

        // Nothing about the chain's duty has changed, so the keepalive has
        // nothing to say. Re-binding it every tick reboots the real chain
        // controller and drops the LCD screens wired behind it.
        var settled = CountBindFrames(tx);
        Assert.True(hub.DriveTick());
        Assert.True(hub.DriveTick());
        Assert.True(hub.DriveTick());
        Assert.Equal(settled, CountBindFrames(tx));
    }

    [Fact]
    public void DriveTick_keeps_quiet_when_the_chain_reports_a_duty_that_never_matches()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());
        Assert.True(hub.Bind(Convert.ToHexString(FanMac)));
        Assert.True(hub.DriveTick());

        // The fake reports fans_pwm as all-zero, exactly like the real
        // firmware, so a reported-duty comparison could never converge and
        // would re-bind on every tick.
        Assert.True(hub.SetPortDuty(Convert.ToHexString(FanMac), 0, 50));
        Assert.True(hub.DriveTick());
        var settled = CountBindFrames(tx);
        Assert.True(hub.DriveTick());
        Assert.True(hub.DriveTick());
        Assert.True(hub.DriveTick());
        Assert.Equal(settled, CountBindFrames(tx));
    }

    [Fact]
    public void DriveTick_re_binds_once_a_port_duty_changes()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());
        Assert.True(hub.Bind(Convert.ToHexString(FanMac)));
        Assert.True(hub.DriveTick());
        var settled = CountBindFrames(tx);

        Assert.True(hub.SetPortDuty(Convert.ToHexString(FanMac), 0, 50));
        Assert.True(hub.DriveTick());
        Assert.True(CountBindFrames(tx) > settled);
    }

    // Bind/PWM frames on the wire: a chunkSeq-0 USB frame carrying RF_Bind.
    private static int CountBindFrames(FakeTxTransport tx)
    {
        var count = 0;
        foreach (var frame in tx.SentFrames)
        {
            if (frame.Length >= 6 && frame[0] == Slv3Protocol.UsbSendRf && frame[1] == 0
                && frame[4] == Slv3Protocol.RfFrameType && frame[5] == Slv3Protocol.RfBind)
            {
                count++;
            }
        }
        return count;
    }

    private static (Slv3Hub Hub, FakeSlv3Network Net, FakeTxTransport Tx, FakeRxTransport Rx) CreateConnectedHub(Func<long>? nowMs = null)
    {
        var net = new FakeSlv3Network();
        var tx = new FakeTxTransport(net);
        var rx = new FakeRxTransport(net);
        var hub = new Slv3Hub(new FakeDiscovery(), port => port.Role == Slv3DongleRole.Tx ? tx : rx, nowMs);
        Assert.True(hub.EnsureConnected());
        return (hub, net, tx, rx);
    }

    // Injectable monotonic clock for chain last-seen/expiry tests.
    private sealed class ManualClock
    {
        private long _nowMs = 1_000_000;
        public long NowMs() => _nowMs;
        public void AdvanceMs(long delta) => _nowMs += delta;
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
        private byte _lastGetMacChannel;

        public FakeTxTransport(FakeSlv3Network net)
        {
            _net = net;
        }

        public bool IsOpen => true;
        public Slv3DongleRole Role => Slv3DongleRole.Tx;
        public string PortName => "fake-tx";
        public List<byte[]> SentFrames { get; } = new();

        // Channel the fake master answers GetMac on. Defaults to the protocol
        // default so a hub that never scans still connects on the first probe.
        public byte MasterChannel { get; set; } = Slv3Protocol.DefaultChannel;

        public bool RfSend(ReadOnlySpan<byte> frame)
        {
            var copy = frame.ToArray();
            SentFrames.Add(copy);
            if (copy.Length >= 2 && copy[0] == Slv3Protocol.UsbGetMac)
            {
                // GetMac and video-start share USB_CMD 0x11; byte [1] is the
                // requested channel for GetMac and a fixed 0x01 for video-start.
                // Only MasterInitLocked's probe/scan reads the reply that follows,
                // so a video-start's byte [1] never gets mistaken for a channel.
                _lastGetMacChannel = copy[1];
            }
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
            if (_lastGetMacChannel == MasterChannel)
            {
                _net.MasterMac.CopyTo(reply, 1);
            }
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
        public List<byte[]> SentFrames { get; } = new();

        // Simulates a wedged RX MCU: the GetDev write succeeds but the reply
        // carries no valid echo, driving the 5-consecutive-failure reset path.
        public bool FailReads { get; set; }

        // Simulates a dead USB handle: the GetDev write itself fails, which
        // DriveTick treats as fatal (no reset streak, immediate false).
        public bool FailSend { get; set; }

        public bool RfSend(ReadOnlySpan<byte> frame)
        {
            var copy = frame.ToArray();
            SentFrames.Add(copy);
            if (FailSend && copy.Length >= 1 && copy[0] == Slv3Protocol.UsbSendRf)
            {
                return false;
            }
            return true;
        }

        public byte[] RfRead(int expectedLen)
        {
            if (FailReads)
            {
                return Array.Empty<byte>();
            }
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
            // Firmware sends only the requested pages: the header still reports the
            // true device count, but records past PageLength * pageCount bytes are
            // truncated. A one-page poll of >10 fans therefore drops the overflow.
            return buf.Length <= expectedLen ? buf : buf[..expectedLen];
        }

        public void Dispose()
        {
        }
    }
}
