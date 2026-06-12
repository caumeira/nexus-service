using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting.Smart;
using Nexus.Service.Lighting.Smart.Discovery;
using Nexus.Service.Lighting.Smart.Drivers.Nanoleaf;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.SmartLights;

/// <summary>End-to-end driver tests against the loopback Nanoleaf emulator:
/// pair → layout capture → control → extControl v2 streaming, no hardware.</summary>
public class NanoleafDriverTests : IDisposable
{
    private readonly string _dir;
    private readonly FakeNanoleafDevice _device = new();
    private readonly NanoleafDriver _driver;

    public NanoleafDriverTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-nl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var store = new JsonConfigStore(Path.Combine(_dir, "settings.json"));
        _driver = new NanoleafDriver(new NanoleafClient(), new LanDiscovery(new MdnsQuery()), store,
            restPort: _device.HttpPort, streamPort: _device.UdpPort);

        // 3 light panels + the Shapes controller (shapeType 12, never lights up).
        _device.Panels = new List<(int, int, int, int)>
        {
            (101, 0, 0, 8),
            (102, 100, 0, 8),
            (103, 100, 100, 8),
            (900, 50, 50, 12),
        };
    }

    private static DiscoveredLight Target => new("nanoleaf", "127.0.0.1", "Nanoleaf", "127.0.0.1");

    private async Task<SmartLight> PairedAsync()
    {
        var result = await _driver.PairAsync(Target, CancellationToken.None);
        Assert.True(result.Ok, result.Error);
        var cfg = Assert.Single(result.Devices);
        return new SmartLight
        {
            Id = cfg.Id,
            Brand = cfg.Brand,
            Name = cfg.Name,
            Host = cfg.Host,
            StableKey = cfg.StableKey,
            Token = cfg.Token,
            Extra = cfg.Extra,
        };
    }

    [Fact]
    public async Task Pair_outsidePairingWindow_reportsPairingMode()
    {
        _device.PairingWindowOpen = false;
        var result = await _driver.PairAsync(Target, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal("pairing-mode", result.Error);
    }

    [Fact]
    public async Task Pair_capturesLayout_filtersNonLightShapes()
    {
        var dev = await PairedAsync();
        Assert.Equal("nanoleaf:S16331A0217", dev.Id);
        Assert.Equal("Office Shapes", dev.Name);
        Assert.Equal("S16331A0217", dev.StableKey);

        var extra = JsonSerializer.Deserialize(dev.Extra, NanoleafJsonContext.Default.NanoleafExtra)!;
        Assert.Equal(NanoleafExtra.KindPanels, extra.Kind);
        Assert.Equal(new[] { 101, 102, 103 }, extra.PanelIds); // controller filtered out
        Assert.Equal(_device.HttpPort, extra.Port);
        Assert.Equal(_device.UdpPort, extra.StreamPort);

        // Centroid UVs, v flipped (layout y is up, canvas v is down).
        Assert.Equal(new[] { 0f, 1f, 1f }, extra.U);
        Assert.Equal(new[] { 1f, 1f, 0f }, extra.V);

        var plan = _driver.PlanFrames(dev);
        Assert.Equal(3, plan.LedCount);
        Assert.False(plan.AverageToSingle);
        Assert.Equal(extra.U, plan.LedU);
        Assert.Equal(extra.V, plan.LedV);
    }

    [Fact]
    public async Task Pair_essentials_usesLedCount()
    {
        _device.Panels = new List<(int, int, int, int)>();
        _device.NumLeds = 24;
        var dev = await PairedAsync();

        var extra = JsonSerializer.Deserialize(dev.Extra, NanoleafJsonContext.Default.NanoleafExtra)!;
        Assert.Equal(NanoleafExtra.KindLeds, extra.Kind);
        Assert.Equal(24, extra.LedCount);

        var plan = _driver.PlanFrames(dev);
        Assert.Equal(24, plan.LedCount);
        Assert.False(plan.AverageToSingle);
        Assert.Null(plan.LedU); // strips sample linearly along the canvas rect
    }

    [Fact]
    public async Task Send_static_putsHsbState_withOnLast()
    {
        var dev = await PairedAsync();
        await _driver.SendAsync(dev, new LightFrame(On: true, 255, 0, 0, 0.5f), CancellationToken.None);

        var body = Assert.Single(_device.StateBodies);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(0, doc.RootElement.GetProperty("hue").GetProperty("value").GetInt32());
        Assert.Equal(100, doc.RootElement.GetProperty("sat").GetProperty("value").GetInt32());
        Assert.Equal(50, doc.RootElement.GetProperty("brightness").GetProperty("value").GetInt32());
        Assert.True(doc.RootElement.GetProperty("on").GetProperty("value").GetBoolean());
        // The device is order-sensitive: "on" must serialize after the rest.
        Assert.True(body.IndexOf("\"on\"", StringComparison.Ordinal) >
                    body.IndexOf("\"brightness\"", StringComparison.Ordinal));

        await _driver.SendAsync(dev, new LightFrame(On: false, 0, 0, 0, 0f), CancellationToken.None);
        using var off = JsonDocument.Parse(_device.StateBodies.Last());
        Assert.False(off.RootElement.GetProperty("on").GetProperty("value").GetBoolean());
    }

    [Fact]
    public async Task Send_zones_enablesExtControl_andStreamsPanels()
    {
        var dev = await PairedAsync();
        var zones = new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255 };

        await _driver.SendAsync(dev, new LightFrame(On: true, 85, 85, 85, 0.5f, zones), CancellationToken.None);
        await TestWait.ForAsync(() => !_device.UdpPackets.IsEmpty);

        // Entering streaming pins brightness to 100 (color carries dimming).
        using (var doc = JsonDocument.Parse(_device.StateBodies.Single()))
        {
            Assert.Equal(100, doc.RootElement.GetProperty("brightness").GetProperty("value").GetInt32());
        }
        using (var doc = JsonDocument.Parse(_device.EffectsBodies.Single()))
        {
            var write = doc.RootElement.GetProperty("write");
            Assert.Equal("display", write.GetProperty("command").GetString());
            Assert.Equal("extControl", write.GetProperty("animType").GetString());
            Assert.Equal("v2", write.GetProperty("extControlVersion").GetString());
        }

        var pkt = _device.UdpPackets.Single();
        Assert.Equal(2 + 3 * 8, pkt.Length);
        Assert.Equal(3, (pkt[0] << 8) | pkt[1]);
        // Zone 0: panelId 101 big-endian, half-bright red, W=0, transition 1.
        Assert.Equal(101, (pkt[2] << 8) | pkt[3]);
        Assert.Equal(new byte[] { 128, 0, 0, 0 }, pkt[4..8]);
        Assert.Equal(1, (pkt[8] << 8) | pkt[9]);
        // Zone 2: panelId 103, half-bright blue.
        Assert.Equal(103, (pkt[18] << 8) | pkt[19]);
        Assert.Equal(new byte[] { 0, 0, 128 }, pkt[20..23]);

        // Second zone frame: no extra REST calls, one more datagram.
        await _driver.SendAsync(dev, new LightFrame(On: true, 85, 85, 85, 0.5f, zones), CancellationToken.None);
        await TestWait.ForAsync(() => _device.UdpPackets.Count == 2);
        Assert.Single(_device.EffectsBodies);

        // Static frame exits streaming via a hue/sat state PUT.
        await _driver.SendAsync(dev, new LightFrame(On: true, 0, 255, 0, 1f), CancellationToken.None);
        Assert.Equal(2, _device.StateBodies.Count);
        using (var doc = JsonDocument.Parse(_device.StateBodies.Last()))
        {
            Assert.Equal(120, doc.RootElement.GetProperty("hue").GetProperty("value").GetInt32());
        }

        // Next zone frame re-enables extControl.
        await _driver.SendAsync(dev, new LightFrame(On: true, 85, 85, 85, 0.5f, zones), CancellationToken.None);
        await TestWait.ForAsync(() => _device.EffectsBodies.Count == 2);
    }

    [Fact]
    public async Task Send_zones_ledKind_addressesLedIndices()
    {
        _device.Panels = new List<(int, int, int, int)>();
        _device.NumLeds = 3;
        var dev = await PairedAsync();

        var zones = new byte[] { 10, 20, 30, 40, 50, 60, 70, 80, 90 };
        await _driver.SendAsync(dev, new LightFrame(On: true, 0, 0, 0, 1f, zones), CancellationToken.None);
        await TestWait.ForAsync(() => !_device.UdpPackets.IsEmpty);

        var pkt = _device.UdpPackets.Single();
        Assert.Equal(0, (pkt[2] << 8) | pkt[3]);   // LED index 0
        Assert.Equal(1, (pkt[10] << 8) | pkt[11]); // LED index 1
        Assert.Equal(new byte[] { 40, 50, 60 }, pkt[12..15]);
    }

    [Fact]
    public async Task Identify_and_Ping_work()
    {
        var dev = await PairedAsync();
        await _driver.IdentifyAsync(dev, CancellationToken.None);
        Assert.Equal(1, _device.IdentifyCount);
        Assert.True(await _driver.PingAsync(dev, CancellationToken.None));
    }

    [Fact]
    public void NormalizeLayout_singlePanel_centers()
    {
        var (u, v) = NanoleafDriver.NormalizeLayout(new List<NanoleafPanelPosition>
        {
            new() { PanelId = 1, X = 42, Y = 42 },
        });
        Assert.Equal(new[] { 0.5f }, u);
        Assert.Equal(new[] { 0.5f }, v);
    }

    public void Dispose()
    {
        _device.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
