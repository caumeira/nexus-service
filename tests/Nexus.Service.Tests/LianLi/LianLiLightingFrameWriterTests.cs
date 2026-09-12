using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Peripherals.LianLi;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.LianLi;

/// <summary>
/// Drives one writer tick against a transport spy and pins the per-family wire
/// sequence, which no hardware on the bench can.
/// </summary>
public class LianLiLightingFrameWriterTests
{
    private readonly LianLiHub _hub = new();
    private readonly HubTransportSpy _spy = new();
    private readonly InMemoryConfigStore _store = new();
    private readonly LightingEngine _engine = new();
    private readonly LianLiLightingFrameWriter _writer;

    public LianLiLightingFrameWriterTests()
    {
        _writer = new LianLiLightingFrameWriter(_engine, _hub, _store, new Np50IdentifyTracker());
    }

    private static LianLiFanProfile Profile(int pid)
    {
        Assert.True(LianLiFanProfiles.TryGet(pid, out var p));
        return p;
    }

    private void Attach(int pid, int port, int fans)
    {
        _hub.Attach(_spy, Profile(pid));
        _store.Update(s =>
        {
            for (var p = 0; p < LianLiProtocol.PortCount; p++) s.Devices.LianLi.SetFans(p, p == port ? fans : 0);
        });
        // The writer only runs once the engine has a device set.
        _engine.UpdateDevices(new[] { new DeviceFrame(0, "lianli:port" + port, 16, 0, 0, 1, 1, 0) });
    }

    private void SetMode(string mode) => _store.Update(s => s.Devices.LianLiLighting.Mode = mode);

    private List<HubTransportSpy.Call> Calls => _spy.Calls;

    [Fact]
    public void Sl_v1_first_tick_clears_merge_and_sets_every_port_quantity_once()
    {
        Attach(0xA100, port: 2, fans: 3);
        SetMode("rainbowWave");

        _writer.Tick();
        var firstTick = Calls.Count;
        _writer.Tick();

        Assert.Equal(new byte[] { 0xE0, 0x10, 0x34, 0x00, 0x00, 0x00, 0x00 }, Calls[0].Bytes);
        Assert.Equal(new byte[] { 0xE0, 0x10, 0x32, 0x00, 0x00, 0x00, 0x00 }, Calls[1].Bytes); // port 0, 0 fans
        Assert.Equal(new byte[] { 0xE0, 0x10, 0x32, 0x10, 0x00, 0x00, 0x00 }, Calls[2].Bytes);
        Assert.Equal(new byte[] { 0xE0, 0x10, 0x32, 0x23, 0x00, 0x00, 0x00 }, Calls[3].Bytes); // port 2, 3 fans
        Assert.Equal(new byte[] { 0xE0, 0x10, 0x32, 0x30, 0x00, 0x00, 0x00 }, Calls[4].Bytes);
        Assert.All(Calls.Take(5), c => Assert.Equal(HubTransportSpy.CallKind.Feature, c.Kind));
        // The second tick re-sends nothing: same sig, hub already initialised.
        Assert.Equal(firstTick, Calls.Count);
    }

    [Fact]
    public void Sl_v1_firmware_mode_commits_one_channel_per_port_without_a_per_frame_start()
    {
        Attach(0xA100, port: 2, fans: 3);
        SetMode("rainbowWave");

        _writer.Tick();
        var calls = Calls.Skip(5).ToList(); // after merge-off + 4 quantities

        // colour (interrupt-OUT) -> commit (feature) -> frame sync (feature)
        Assert.Equal(3, calls.Count);
        Assert.Equal(HubTransportSpy.CallKind.Write, calls[0].Kind);
        Assert.Equal(0x32, calls[0].Bytes[1]); // 0x30 | channel 2 == port 2
        Assert.Equal(HubTransportSpy.CallKind.Feature, calls[1].Kind);
        Assert.Equal(0x12, calls[1].Bytes[1]); // 0x10 | channel 2
        Assert.Equal(0x05, calls[1].Bytes[2]); // rainbow
        Assert.Equal(new byte[] { 0xE0, 0x60, 0x00, 0x01, 0x00, 0x00, 0x00 }, calls[2].Bytes);
    }

    [Fact]
    public void Sl_v1_static_fills_each_fan_ring_with_its_colour_cycling_the_list()
    {
        Attach(0xA100, port: 0, fans: 3);
        _store.Update(s =>
        {
            s.Devices.LianLiLighting.Mode = "static";
            s.Devices.LianLiLighting.Colors = new List<string> { "#FF0000", "#00FF00" };
        });

        _writer.Tick();

        var colour = Calls.Single(c => c.Kind == HubTransportSpy.CallKind.Write).Bytes;
        // wire R,B,G per LED; fan 0 red, fan 1 green, fan 2 red again, 16 LEDs each
        Assert.Equal(new byte[] { 0xFF, 0x00, 0x00 }, colour[2..5]);
        Assert.Equal(new byte[] { 0xFF, 0x00, 0x00 }, colour[(2 + 15 * 3)..(2 + 16 * 3)]);
        Assert.Equal(new byte[] { 0x00, 0x00, 0xFF }, colour[(2 + 16 * 3)..(2 + 17 * 3)]);
        Assert.Equal(new byte[] { 0xFF, 0x00, 0x00 }, colour[(2 + 32 * 3)..(2 + 33 * 3)]);
        Assert.Equal(0x00, colour[2 + 48 * 3]); // fan 3 absent
    }

    [Fact]
    public void Sl_infinity_firmware_mode_commits_inner_and_outer_channels_per_port()
    {
        Attach(0xA102, port: 1, fans: 2);
        SetMode("rainbowWave");

        _writer.Tick();

        // two channels x (start, colour, commit) + one frame sync
        Assert.Equal(7, Calls.Count);
        Assert.Equal(new byte[] { 0xE0, 0x10, 0x60, 0x02, 0x02, 0x00, 0x00 }, Calls[0].Bytes);
        Assert.Equal(HubTransportSpy.CallKind.OutputReport, Calls[1].Kind);
        Assert.Equal(0x32, Calls[1].Bytes[1]); // 0x30 | inner channel 2
        Assert.Equal(0x33, Calls[4].Bytes[1]); // 0x30 | outer channel 3
        Assert.Equal(0x13, Calls[5].Bytes[1]); // 0x10 | outer channel 3
        Assert.Equal(0x60, Calls[6].Bytes[1]);
    }

    [Fact]
    public void Sl_v1_falls_back_to_static_for_a_persisted_sl_infinity_only_mode()
    {
        Attach(0xA100, port: 0, fans: 1);
        SetMode("voice");

        _writer.Tick();

        var commit = Calls.Single(c => c.Kind == HubTransportSpy.CallKind.Feature && (c.Bytes[1] & 0xF0) == 0x10 && c.Bytes[2] is not (0x32 or 0x34));
        Assert.Equal(0x01, commit.Bytes[2]); // static, not 0x26
    }

    [Fact]
    public void Sl_v1_custom_mode_streams_port_3_on_channel_3()
    {
        Attach(0xA100, port: 3, fans: 4);
        SetMode("custom");

        _writer.Tick();
        var calls = Calls.Skip(5).ToList();

        Assert.Equal(3, calls.Count);
        Assert.Equal(HubTransportSpy.CallKind.Write, calls[0].Kind);
        Assert.Equal(0x33, calls[0].Bytes[1]); // 0x30 | channel 3, not channel 1
        Assert.Equal(LianLiProtocol.OutputReportSize, calls[0].Bytes.Length);
        Assert.Equal(0x13, calls[1].Bytes[1]);
        Assert.Equal(0x01, calls[1].Bytes[2]); // static latches the streamed frame
        Assert.Equal(0x60, calls[2].Bytes[1]);
    }
}
