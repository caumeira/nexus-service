using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting.Smart;
using Nexus.Service.Lighting.Smart.Discovery;
using Nexus.Service.Lighting.Smart.Drivers.Hue;
using Xunit;

namespace Nexus.Service.Tests.SmartLights;

/// <summary>
/// Unit-level HueDriver coverage against a real (socket-bound) fake bridge -
/// see FakeHueBridge for why a TestServer can't stand in. No test here reaches
/// a real Hue bridge; the REST fallback rendering a fill on hardware with no
/// Entertainment Area is verified by construction only.
/// </summary>
public class HueDriverTests
{
    private static SmartLight MakeDev(string host, string rid = "light1") => new()
    {
        Id = "hue:bridge1:" + rid,
        Brand = "hue",
        Name = "Test Light",
        Host = host,
        StableKey = "bridge1",
        Token = "test-app-key",
        Extra = HueDriver.BuildExtra(rid, "deadbeef"),
    };

    [Fact]
    public async Task SendAsync_skipsUnchangedState_butSendsOnChange()
    {
        await using var bridge = new FakeHueBridge();
        await bridge.StartAsync();
        var driver = new HueDriver(new HueBridgeClient(), new LanDiscovery(new MdnsQuery()));
        var dev = MakeDev(bridge.Host);

        var frame = new LightFrame(On: true, 10, 20, 30, 1f);
        await driver.SendAsync(dev, frame, CancellationToken.None);
        Assert.Single(bridge.Puts); // first send always goes out

        // Same on/off + RGB + brightness -> the bulb already holds this state.
        await driver.SendAsync(dev, frame, CancellationToken.None);
        Assert.Single(bridge.Puts); // deduped, no second PUT

        var changed = new LightFrame(On: true, 10, 20, 30, 0.5f); // brightness-only change
        await driver.SendAsync(dev, changed, CancellationToken.None);
        Assert.Equal(2, bridge.Puts.Count); // meaningful change -> resent
    }

    [Fact]
    public async Task SendAsync_dedupeKey_coversOnOff_notJustRgb()
    {
        await using var bridge = new FakeHueBridge();
        await bridge.StartAsync();
        var driver = new HueDriver(new HueBridgeClient(), new LanDiscovery(new MdnsQuery()));
        var dev = MakeDev(bridge.Host);

        await driver.SendAsync(dev, new LightFrame(On: true, 10, 20, 30, 1f), CancellationToken.None);
        Assert.Single(bridge.Puts);

        // Same RGB/brightness, power flips off -> must not be swallowed by an
        // RGB-only dedupe key.
        await driver.SendAsync(dev, new LightFrame(On: false, 10, 20, 30, 1f), CancellationToken.None);
        Assert.Equal(2, bridge.Puts.Count);
    }

    [Fact]
    public void CanStream_falseDuringBackoff_trueOnceExpired()
    {
        var driver = new HueDriver(new HueBridgeClient(), new LanDiscovery(new MdnsQuery()));
        var dev = MakeDev("10.0.0.1");

        Assert.True(driver.CanStream(dev)); // never failed -> stream is attempted

        var failedUntil = (System.Collections.Generic.Dictionary<string, long>)
            typeof(HueDriver).GetField("_failedUntil", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(driver)!;
        failedUntil[dev.Host] = Environment.TickCount64 + 10_000;
        Assert.False(driver.CanStream(dev)); // inside the retry backoff

        failedUntil[dev.Host] = Environment.TickCount64 - 1;
        Assert.True(driver.CanStream(dev)); // backoff elapsed -> retry allowed
    }

    [Fact]
    public async Task StopAll_clearsDedupeState_soARestartResendsTheFirstFrame()
    {
        await using var bridge = new FakeHueBridge();
        await bridge.StartAsync();
        var driver = new HueDriver(new HueBridgeClient(), new LanDiscovery(new MdnsQuery()));
        var dev = MakeDev(bridge.Host);
        var frame = new LightFrame(On: true, 10, 20, 30, 1f);

        await driver.SendAsync(dev, frame, CancellationToken.None);
        Assert.Single(bridge.Puts);

        driver.StopAll();

        await driver.SendAsync(dev, frame, CancellationToken.None); // identical state
        Assert.Equal(2, bridge.Puts.Count); // not deduped across a StopAll
    }
}
