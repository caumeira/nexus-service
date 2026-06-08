using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting.Smart;
using Nexus.Service.Lighting.Smart.Discovery;
using Nexus.Service.Lighting.Smart.Drivers.Hue;
using Xunit;

namespace Nexus.Service.Tests.SmartLights;

/// <summary>
/// Live tests against a real Hue bridge on the LAN. Opt-in only:
///   NEXUS_HUE_LIVE=&lt;bridge-ip&gt; dotnet test --filter "Category=Manual"
/// Category=Manual keeps them out of the CI gate; they also self-skip when the
/// env var is absent. Everything here works WITHOUT pressing the bridge button
/// except a successful pair — pre-press pairing is expected to return the
/// link-button error, which is what we assert.
/// </summary>
[Trait("Category", "Manual")]
public class HueLiveTests
{
    private static string? Bridge => Environment.GetEnvironmentVariable("NEXUS_HUE_LIVE");

    [Fact]
    public async Task BridgeConfig_isReachable_andHasBridgeId()
    {
        var host = Bridge;
        if (string.IsNullOrEmpty(host)) return; // self-skip without env

        var client = new HueBridgeClient();
        var cfg = await client.GetBridgeConfigAsync(host, CancellationToken.None);
        Assert.NotNull(cfg);
        Assert.False(string.IsNullOrEmpty(cfg!.BridgeId));
    }

    [Fact]
    public async Task Discover_findsAtLeastOneBridge()
    {
        if (string.IsNullOrEmpty(Bridge)) return;

        var driver = new HueDriver(new HueBridgeClient(), new LanDiscovery(new MdnsQuery()));
        var found = await driver.DiscoverAsync(CancellationToken.None);
        Assert.NotEmpty(found);
    }

    [Fact]
    public async Task Pair_beforeButtonPress_returnsLinkButtonError()
    {
        var host = Bridge;
        if (string.IsNullOrEmpty(host)) return;

        var driver = new HueDriver(new HueBridgeClient(), new LanDiscovery(new MdnsQuery()));
        var result = await driver.PairAsync(new DiscoveredLight("hue", host, "Bridge", ""), CancellationToken.None);

        // No app key stored yet + button not pressed → link-button error.
        Assert.False(result.Ok);
        Assert.Equal("link-button", result.Error);
    }
}
