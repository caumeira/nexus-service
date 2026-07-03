using System;
using System.Collections.Generic;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Peripherals.LianLiWireless;
using Xunit;

namespace Nexus.Service.Tests.LianLiWireless;

public class Slv3LightingFrameWriterTests
{
    private static readonly byte[] FanMac = Convert.FromHexString("112233445566");

    private static (Slv3Hub Hub, Slv3TestHub.FakeSlv3Network Net, Slv3TestHub.FakeTxTransport Tx,
        Slv3LightingDeviceProvider Provider, LightingEngine Engine, Slv3LightingFrameWriter Writer, List<DeviceFrame> Frames)
        CreateBoundSetup()
    {
        var (hub, net, tx) = Slv3TestHub.CreateConnected();
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, FanCount = 1 });
        Assert.True(hub.DriveTick());

        var store = new InMemoryConfigStore();
        var identify = new Np50IdentifyTracker();
        var provider = new Slv3LightingDeviceProvider(hub, store, identify);
        var frames = new List<DeviceFrame>(provider.BuildFrames(0));

        var engine = new LightingEngine();
        engine.UpdateDevices(frames.ToArray());

        var writer = new Slv3LightingFrameWriter(engine, hub, store, identify, provider);
        return (hub, net, tx, provider, engine, writer, frames);
    }

    private static void FillAll(IReadOnlyList<DeviceFrame> frames, byte r, byte g, byte b)
    {
        foreach (var frame in frames)
        {
            frame.Fill(r, g, b);
        }
    }

    private static List<byte[]> RgbSyncFrames(Slv3TestHub.FakeTxTransport tx) =>
        tx.SentFrames.FindAll(f => f.Length >= 6 && f[1] == 0 && f[4] == Slv3Protocol.RfFrameType && f[5] == Slv3Protocol.RfRgbSync);

    [Fact]
    public void Tick_streams_composed_colors_to_a_bound_fan_chain()
    {
        var (_, _, tx, _, _, writer, frames) = CreateBoundSetup();
        FillAll(frames, 10, 20, 30);

        writer.Tick();

        // Header packet sent 4x plus at least one data packet.
        Assert.True(RgbSyncFrames(tx).Count >= 5);
    }

    [Fact]
    public void Tick_does_not_resend_once_content_is_unchanged_and_confirmed()
    {
        var (hub, _, tx, _, _, writer, frames) = CreateBoundSetup();
        FillAll(frames, 10, 20, 30);

        writer.Tick();
        Assert.True(hub.DriveTick()); // RX now echoes the effect_index we just sent

        var countAfterFirst = tx.SentFrames.Count;
        writer.Tick();

        Assert.Equal(countAfterFirst, tx.SentFrames.Count);
    }

    [Fact]
    public void Tick_resends_when_composed_content_changes()
    {
        var (hub, _, tx, _, _, writer, frames) = CreateBoundSetup();
        FillAll(frames, 10, 20, 30);
        writer.Tick();
        Assert.True(hub.DriveTick());
        var countAfterFirst = tx.SentFrames.Count;

        FillAll(frames, 200, 50, 5);
        writer.Tick();

        Assert.True(tx.SentFrames.Count > countAfterFirst);
    }

    [Fact]
    public void Tick_does_nothing_when_engine_has_no_devices()
    {
        var (_, _, tx, _, engine, writer, _) = CreateBoundSetup();
        engine.UpdateDevices(Array.Empty<DeviceFrame>());

        writer.Tick();

        // The prior DriveTick() in CreateBoundSetup already sent bind/heartbeat
        // frames over the same fake TX; only RF_RgbSync frames are this writer's concern.
        Assert.Empty(RgbSyncFrames(tx));
    }

    [Fact]
    public void Tick_clears_cache_on_disconnect_so_reconnect_resends_unchanged_content()
    {
        var (hub, net, tx, _, _, writer, frames) = CreateBoundSetup();
        FillAll(frames, 10, 20, 30);
        writer.Tick();
        Assert.True(hub.DriveTick());
        var countAfterFirst = tx.SentFrames.Count;

        hub.Disconnect();
        writer.Tick(); // hub disconnected: no-op, but clears the writer's cache
        Assert.Equal(countAfterFirst, tx.SentFrames.Count);

        Assert.True(hub.EnsureConnected());
        net.Fans.Clear();
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, FanCount = 1 });
        Assert.True(hub.DriveTick());

        writer.Tick();
        Assert.True(tx.SentFrames.Count > countAfterFirst);
    }
}
