using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting.Smart;
using Nexus.Service.Models.SmartLights;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.SmartLights;

public class SmartLightProviderTests : IDisposable
{
    private readonly string _dir;
    private readonly JsonConfigStore _store;
    private readonly NetworkSendThrottle _throttle = new();
    private readonly FakeDriver _driver = new();
    private readonly SmartLightProvider _provider;

    public SmartLightProviderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-sl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new JsonConfigStore(Path.Combine(_dir, "settings.json"));
        _provider = new SmartLightProvider(new ILightDriver[] { _driver }, _store, _throttle);
    }

    [Fact]
    public void Owns_matchesBrandPrefix()
    {
        Assert.True(_provider.Owns("fake:bridge:1"));
        Assert.False(_provider.Owns("hue:bridge:1"));   // no hue driver registered here
        Assert.False(_provider.Owns("openrgb-0"));
        Assert.False(_provider.Owns(""));
    }

    [Fact]
    public void GetAll_emptyWhenNonePaired()
    {
        var all = _provider.GetAll();
        Assert.False(all.IsInit);
        Assert.Empty(all.Devices);
        Assert.False(_provider.IsConnected);
    }

    [Fact]
    public async Task Pair_persistsDevices_andSurfacesCard()
    {
        var res = await _provider.PairAsync(
            new PairSmartLightBody { Brand = "fake", Host = "1.2.3.4", StableKey = "bridge" }, CancellationToken.None);

        Assert.True(res.Ok);
        Assert.Equal(1, res.Added);
        Assert.True(_provider.IsConnected);

        var all = _provider.GetAll();
        var card = Assert.Single(all.Devices);
        Assert.Equal("fake:bridge:1", card.Id);
        Assert.Equal("L1", card.Name);
        Assert.True(card.LedsOn);

        // Re-pair is idempotent (updates in place, adds 0).
        var again = await _provider.PairAsync(
            new PairSmartLightBody { Brand = "fake", Host = "1.2.3.4", StableKey = "bridge" }, CancellationToken.None);
        Assert.True(again.Ok);
        Assert.Equal(0, again.Added);
        Assert.Single(_provider.GetAll().Devices);
    }

    [Fact]
    public async Task SetPower_off_marksCardOff_andRemove_clears()
    {
        await _provider.PairAsync(
            new PairSmartLightBody { Brand = "fake", Host = "1.2.3.4", StableKey = "bridge" }, CancellationToken.None);

        _provider.SetPower("fake:bridge:1", false);
        Assert.False(_provider.GetAll().Devices[0].LedsOn);

        _provider.SetPower("fake:bridge:1", true);
        Assert.True(_provider.GetAll().Devices[0].LedsOn);

        _provider.Remove("fake:bridge:1");
        Assert.Empty(_provider.GetAll().Devices);
    }

    [Fact]
    public void BuildFrames_buildsAveragingGrid()
    {
        // Seed config directly, then build frames.
        _store.Update(s => s.SmartLights.Devices.Add(new SmartLightConfig
        {
            Id = "fake:bridge:1", Brand = "fake", Name = "L1", Host = "1.2.3.4", StableKey = "bridge", Extra = "1",
        }));

        var frames = _provider.BuildFrames(7);
        var frame = Assert.Single(frames);
        Assert.Equal(7, frame.Index);
        Assert.Equal("fake:bridge:1", frame.Id);
        Assert.Equal(16, frame.LedCount);
        Assert.NotNull(frame.LedU);
        Assert.NotNull(frame.LedV);
        Assert.Equal(16, frame.LedU!.Length);
    }

    public void Dispose()
    {
        _throttle.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class FakeDriver : ILightDriver
    {
        public string Brand => "fake";
        public Task<IReadOnlyList<DiscoveredLight>> DiscoverAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DiscoveredLight>>(new[] { new DiscoveredLight("fake", "1.2.3.4", "Fake", "bridge") });
        public Task<PairResult> PairAsync(DiscoveredLight target, CancellationToken ct)
            => Task.FromResult(new PairResult
            {
                Ok = true,
                Devices = new List<SmartLightConfig>
                {
                    new() { Id = "fake:bridge:1", Brand = "fake", Name = "L1", Host = target.Host, StableKey = "bridge", Token = "t", Extra = "1" },
                },
            });
        public LightFramePlan PlanFrames(SmartLight dev) => new(16, true);
        public Task SendAsync(SmartLight dev, LightFrame frame, CancellationToken ct) => Task.CompletedTask;
        public Task IdentifyAsync(SmartLight dev, CancellationToken ct) => Task.CompletedTask;
        public int MinIntervalMs(SmartLight dev) => 10;
        public string RateLimitKey(SmartLight dev) => dev.Id;
        public Task<bool> PingAsync(SmartLight dev, CancellationToken ct) => Task.FromResult(true);
    }
}
