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

    [Fact]
    public void BuildFrames_usesDriverUvMap_whenPlanProvidesOne()
    {
        _driver.Plan = new LightFramePlan(2, AverageToSingle: false,
            LedU: new[] { 0.1f, 0.9f }, LedV: new[] { 0.2f, 0.8f });
        _store.Update(s => s.SmartLights.Devices.Add(new SmartLightConfig
        {
            Id = "fake:bridge:1", Brand = "fake", Name = "L1", Host = "1.2.3.4", StableKey = "bridge", Extra = "1",
        }));

        var frame = Assert.Single(_provider.BuildFrames(0));
        Assert.Equal(2, frame.LedCount);
        Assert.Equal(new[] { 0.1f, 0.9f }, frame.LedU);
        Assert.Equal(new[] { 0.2f, 0.8f }, frame.LedV);
    }

    [Fact]
    public async Task SubmitEffectFrame_attachesZones_forZonePlans()
    {
        _driver.Plan = new LightFramePlan(3, AverageToSingle: false);
        _store.Update(s => s.SmartLights.Devices.Add(new SmartLightConfig
        {
            Id = "fake:bridge:1", Brand = "fake", Name = "L1", Host = "1.2.3.4", StableKey = "bridge", Extra = "1",
        }));
        _provider.BuildFrames(0); // captures the plan + populates the cache

        var tcs = new TaskCompletionSource<LightFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        _driver.OnSend = f => tcs.TrySetResult(f);

        var leds = new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255 };
        _provider.SubmitEffectFrame("fake:bridge:1", leds, 3, 0.5f);

        var sent = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(sent.On);
        Assert.Equal(leds, sent.Zones);
        Assert.Equal((byte)85, sent.R); // channel averages across the 3 zones
        Assert.Equal((byte)85, sent.G);
        Assert.Equal((byte)85, sent.B);
        Assert.Equal(0.5f, sent.Brightness01);
    }

    [Fact]
    public async Task SubmitEffectFrame_noZones_forAveragingPlans()
    {
        _store.Update(s => s.SmartLights.Devices.Add(new SmartLightConfig
        {
            Id = "fake:bridge:1", Brand = "fake", Name = "L1", Host = "1.2.3.4", StableKey = "bridge", Extra = "1",
        }));
        _provider.BuildFrames(0);

        var tcs = new TaskCompletionSource<LightFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        _driver.OnSend = f => tcs.TrySetResult(f);

        _provider.SubmitEffectFrame("fake:bridge:1", new byte[] { 10, 20, 30 }, 1, 1f);

        var sent = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(sent.Zones);
        Assert.Equal((byte)10, sent.R);
        Assert.Equal((byte)20, sent.G);
        Assert.Equal((byte)30, sent.B);
    }

    [Fact]
    public async Task GetAll_hidesOfflineDevice_andRestoresOnReconnect()
    {
        await _provider.PairAsync(
            new PairSmartLightBody { Brand = "fake", Host = "1.2.3.4", StableKey = "bridge" }, CancellationToken.None);

        _driver.PingResult = true;
        await _provider.GetSmartLightDtosAsync(CancellationToken.None);
        Assert.Single(_provider.GetAll().Devices);   // online -> shown

        _driver.PingResult = false;
        await _provider.GetSmartLightDtosAsync(CancellationToken.None);
        Assert.Empty(_provider.GetAll().Devices);     // offline -> card hidden

        _driver.PingResult = true;
        await _provider.GetSmartLightDtosAsync(CancellationToken.None);
        Assert.Single(_provider.GetAll().Devices);   // reconnect -> shown again
    }

    [Fact]
    public async Task OnlineProbe_raisesOnlineChanged_onlyOnTransition()
    {
        await _provider.PairAsync(
            new PairSmartLightBody { Brand = "fake", Host = "1.2.3.4", StableKey = "bridge" }, CancellationToken.None);
        var events = 0;
        _provider.OnlineChanged += () => Interlocked.Increment(ref events);

        _driver.PingResult = true;
        await _provider.GetSmartLightDtosAsync(CancellationToken.None);  // absent->online: no transition
        _driver.PingResult = false;
        await _provider.GetSmartLightDtosAsync(CancellationToken.None);  // online->offline: +1
        await _provider.GetSmartLightDtosAsync(CancellationToken.None);  // still offline: no event
        _driver.PingResult = true;
        await _provider.GetSmartLightDtosAsync(CancellationToken.None);  // offline->online: +1

        Assert.Equal(2, events);
    }

    [Fact]
    public async Task BuildFrames_stillBuildsOfflineDevice()
    {
        await _provider.PairAsync(
            new PairSmartLightBody { Brand = "fake", Host = "1.2.3.4", StableKey = "bridge" }, CancellationToken.None);
        _driver.PingResult = false;
        await _provider.GetSmartLightDtosAsync(CancellationToken.None);

        Assert.Empty(_provider.GetAll().Devices);   // card hidden
        Assert.Single(_provider.BuildFrames(0));      // engine keeps sending (the reconnect signal)
    }

    [Fact]
    public async Task SuccessfulSend_doesNotReviveOfflineCard()
    {
        // Regression: a fire-and-forget UDP send (Govee) always "succeeds" even
        // to a dead device, so the send path must not mark it online and undo
        // the probe's offline verdict.
        await _provider.PairAsync(
            new PairSmartLightBody { Brand = "fake", Host = "1.2.3.4", StableKey = "bridge" }, CancellationToken.None);
        _provider.BuildFrames(0); // populate the per-tick cache SubmitFrame needs

        _driver.PingResult = false;
        await _provider.GetSmartLightDtosAsync(CancellationToken.None); // probe -> offline
        Assert.Empty(_provider.GetAll().Devices);

        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _driver.OnSend = _ => sent.TrySetResult();
        _provider.SubmitFrame("fake:bridge:1", new LightFrame(On: true, 1, 2, 3, 1f, null));
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Empty(_provider.GetAll().Devices); // still hidden; the send didn't revive it
    }

    [Fact]
    public void GetStructures_exposesLinearStrip_forZoneAddressableLight()
    {
        _driver.Plan = new LightFramePlan(20, AverageToSingle: false);
        _store.Update(s => s.SmartLights.Devices.Add(new SmartLightConfig
        {
            Id = "fake:bridge:1", Brand = "fake", Name = "L1", Host = "1.2.3.4", StableKey = "bridge", Extra = "1",
        }));

        var st = Assert.Single(_provider.GetStructures());
        Assert.Equal("fake:bridge:1", st.DeviceId);
        var seg = Assert.Single(st.Segments);
        Assert.Equal(20, seg.LedCount);
        Assert.Equal(20, seg.FrameLedCount);
        Assert.False(seg.Resizable);
        Assert.Equal("linear", seg.ZoneType);

        var zone = Assert.Single(st.DefaultZones);
        Assert.Equal("fake:bridge:1", zone.Id); // MUST equal the card id so RgbBridge matches the frame
        var slice = Assert.Single(zone.Slices);
        Assert.Equal(0, slice.Segment);
        Assert.Equal(0, slice.Start);
        Assert.Equal(20, slice.Count);
    }

    [Fact]
    public void GetStructures_seedsDriverUv_andSkipsSingleColorLamps()
    {
        // Single-color lamp (default averaging plan) has nothing to arrange.
        _store.Update(s => s.SmartLights.Devices.Add(new SmartLightConfig
        {
            Id = "fake:bridge:1", Brand = "fake", Name = "L1", Host = "1.2.3.4", StableKey = "bridge", Extra = "1",
        }));
        Assert.Empty(_provider.GetStructures());

        // A zone-addressable light's driver sweep seeds the segment defaults.
        _driver.Plan = new LightFramePlan(2, AverageToSingle: false,
            LedU: new[] { 0.1f, 0.9f }, LedV: new[] { 0.5f, 0.5f });
        var seg = Assert.Single(Assert.Single(_provider.GetStructures()).Segments);
        Assert.Equal(new[] { 0.1f, 0.9f }, seg.DefaultU);
        Assert.Equal(new[] { 0.5f, 0.5f }, seg.DefaultV);
    }

    [Fact]
    public async Task ReachabilityPolling_probesAndHidesCard_whenOffline()
    {
        await _provider.PairAsync(
            new PairSmartLightBody { Brand = "fake", Host = "1.2.3.4", StableKey = "bridge" }, CancellationToken.None);

        var flipped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _provider.OnlineChanged += () => flipped.TrySetResult();

        _driver.PingResult = false;
        _provider.StartReachabilityPolling();   // probes immediately, then on interval

        await flipped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _provider.StopReachabilityPolling();
        Assert.Empty(_provider.GetAll().Devices);
    }

    public void Dispose()
    {
        _provider.StopReachabilityPolling();
        _throttle.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class FakeDriver : ILightDriver
    {
        public LightFramePlan Plan = new(16, true);
        public Action<LightFrame>? OnSend;
        public bool PingResult = true;

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
        public LightFramePlan PlanFrames(SmartLight dev) => Plan;
        public Task SendAsync(SmartLight dev, LightFrame frame, CancellationToken ct)
        { OnSend?.Invoke(frame); return Task.CompletedTask; }
        public Task IdentifyAsync(SmartLight dev, CancellationToken ct) => Task.CompletedTask;
        public int MinIntervalMs(SmartLight dev) => 10;
        public string RateLimitKey(SmartLight dev) => dev.Id;
        public Task<bool> PingAsync(SmartLight dev, CancellationToken ct) => Task.FromResult(PingResult);
    }
}
