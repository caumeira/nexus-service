using System;
using System.IO;
using System.Linq;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Monitoring.History.Binary;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History;

/// <summary>
/// The per-app usage behavior IAppUsageHistoryStore.Append/Query must have
/// regardless of which store implements it - window-average-over-distinct-
/// sampled-ticks (not an app's own row count), case-insensitive name dedup
/// with first-seen casing kept, the gpu_series/GpuRingStore dependency for
/// "gpu:&lt;gid&gt;"/"vram:&lt;gid&gt;" rows, bare gpu/vram cross-adapter
/// summing, prune-then-reappear reading fresh, and app key stability across
/// a reopen. Run against both SqliteMetricsHistoryStore
/// (SqliteAppUsageHistorySpecTests) and BinaryMetricsHistoryStore
/// (BinaryAppUsageHistorySpecTests) so a behavior change to either store's
/// app-usage path is pinned once, not twice.
/// </summary>
public abstract class AppUsageHistorySpec : IDisposable
{
    private readonly string _dir;
    protected IAppUsageHistoryStore Store = null!;

    // Both concrete stores implement IMetricsHistoryStore too (the same
    // object Store points at); the scalar side registers gpu_series/
    // GpuRingStore identities the gpu:<gid>/vram:<gid> tests below depend
    // on, so it needs its own Append entry point alongside Store's.
    private IMetricsHistoryStore _scalar = null!;

    protected AppUsageHistorySpec()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-appusagehistory-spec-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        OpenStore();
    }

    protected abstract IAppUsageHistoryStore CreateStore(string dir);

    private void OpenStore()
    {
        var created = CreateStore(_dir);
        Store = created;
        _scalar = (IMetricsHistoryStore)created;
    }

    protected void AppendScalar(params MetricSample[] samples) => _scalar.Append(samples, null);

    /// <summary>Disposes the current store and reopens a fresh instance at
    /// the same directory, simulating a service restart.</summary>
    protected void Reopen()
    {
        (Store as IDisposable)?.Dispose();
        OpenStore();
    }

    public virtual void Dispose()
    {
        (Store as IDisposable)?.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static AppUsageTick CpuTick(long ts, params (string Name, double Value)[] apps) =>
        new(ts, new[] { new AppMetricSample("cpu", apps.Select(a => new AppUsagePoint(a.Name, a.Value, null)).ToList()) });

    private static MetricSample ScalarGpuSample(long ts, string gpuId, string name) =>
        new(ts, null, null, null, null, null,
            new[] { new GpuReading(gpuId, name, "", 50, 60) }, Array.Empty<FanReading>());

    private static AppUsageTick GpuTick(long ts, string gid, params (string Name, double Value, double? Vram)[] apps) =>
        new(ts, new[] { new AppMetricSample($"gpu:{gid}", apps.Select(a => new AppUsagePoint(a.Name, a.Value, a.Vram)).ToList()) });

    private static AppUsageTick VramTick(long ts, string gid, params (string Name, double Value)[] apps) =>
        new(ts, new[] { new AppMetricSample($"vram:{gid}", apps.Select(a => new AppUsagePoint(a.Name, a.Value, null)).ToList()) });

    private static AppUsageTick MultiAdapterVramTick(
        long ts, string gid0, (string Name, double Value) app0, string gid1, (string Name, double Value) app1) =>
        new(ts, new[]
        {
            new AppMetricSample($"vram:{gid0}", new[] { new AppUsagePoint(app0.Name, app0.Value, null) }),
            new AppMetricSample($"vram:{gid1}", new[] { new AppUsagePoint(app1.Name, app1.Value, null) }),
        });

    // A real tick (ProcessAppUsageSource.Sample) carries every metric -
    // cpu, memory, and one "gpu:<gid>" sample per adapter - as one
    // AppMetricSample list on a single AppUsageTick, not as separate ticks
    // sharing a ts.
    private static AppUsageTick MultiAdapterGpuTick(
        long ts, string gid0, (string Name, double Value, double? Vram) app0,
        string gid1, (string Name, double Value, double? Vram) app1) =>
        new(ts, new[]
        {
            new AppMetricSample($"gpu:{gid0}", new[] { new AppUsagePoint(app0.Name, app0.Value, app0.Vram) }),
            new AppMetricSample($"gpu:{gid1}", new[] { new AppUsagePoint(app1.Name, app1.Value, app1.Vram) }),
        });

    [Fact]
    public void Append_then_QueryTopApps_RoundTripsCpuValues()
    {
        Store.Append(new[] { CpuTick(1000, ("app.exe", 42.3)) }, null);

        var top = Store.QueryTopApps("cpu", 0, 10_000, 15);

        var app = Assert.Single(top);
        Assert.Equal("app.exe", app.Name);
        Assert.Equal(42.3, app.Avg);
        Assert.Equal(42.3, app.Max);
    }

    [Fact]
    public void Append_then_QueryAppSeries_RoundTripsRawPoints_AscendingByTs()
    {
        Store.Append(new[] { CpuTick(2000, ("app.exe", 20)), CpuTick(1000, ("app.exe", 10)) }, null);

        var points = Store.QueryAppSeries("cpu", "app.exe", 0, 10_000);

        Assert.Equal(new long[] { 1000, 2000 }, points.Select(p => p.TsSec).ToArray());
        Assert.Equal(10, points[0].Value);
        Assert.Equal(20, points[1].Value);
    }

    [Fact]
    public void QueryTopApps_OrdersByAvgDescending_AndLimitsToMaxApps()
    {
        Store.Append(new[] { CpuTick(1000, ("low", 5), ("high", 90), ("mid", 40)) }, null);

        var top = Store.QueryTopApps("cpu", 0, 10_000, 2);

        Assert.Equal(new[] { "high", "mid" }, top.Select(a => a.Name).ToArray());
    }

    [Fact]
    public void QueryTopApps_ComputesWindowAverage_AcrossMultipleTicks()
    {
        Store.Append(new[] { CpuTick(1000, ("app.exe", 10)), CpuTick(1005, ("app.exe", 30)) }, null);

        var app = Assert.Single(Store.QueryTopApps("cpu", 0, 10_000, 15));

        Assert.Equal(20, app.Avg);
        Assert.Equal(30, app.Max);
    }

    [Fact]
    public void QueryTopApps_RanksASustainedLowerLoad_AboveASingleTickSpike()
    {
        // "spike" only ever appears in one of five sampled ticks (it fell
        // out of the top-N the rest of the time); "sustained" appears in
        // all five at a lower value. Dividing by the app's own row count
        // would let the single 100% tick outrank the steady 40% load - the
        // window average must divide by the ticks the metric was actually
        // sampled, not by how many of them this app happened to rank into.
        Store.Append(new[]
        {
            CpuTick(1000, ("spike", 100), ("sustained", 40)),
            CpuTick(1005, ("sustained", 40)),
            CpuTick(1010, ("sustained", 40)),
            CpuTick(1015, ("sustained", 40)),
            CpuTick(1020, ("sustained", 40)),
        }, null);

        var top = Store.QueryTopApps("cpu", 0, 10_000, 15);

        Assert.Equal("sustained", top.First().Name);
        var sustained = top.Single(a => a.Name == "sustained");
        Assert.Equal(40, sustained.Avg);
        var spike = top.Single(a => a.Name == "spike");
        Assert.Equal(20, spike.Avg); // 100 / 5 sampled ticks, not 100 / 1 own row
    }

    [Fact]
    public void QuerySampledTicks_ReturnsDistinctTimestamps_ForTheMetric()
    {
        Store.Append(new[] { CpuTick(1000, ("a", 1)), CpuTick(1005, ("a", 1), ("b", 1)) }, null);

        var ticks = Store.QuerySampledTicks("cpu", 0, 10_000);

        Assert.Equal(new long[] { 1000, 1005 }, ticks);
    }

    [Fact]
    public void QuerySampledTicks_ReturnsEmpty_ForAnUnrecognizedMetric()
    {
        Store.Append(new[] { CpuTick(1000, ("a", 1)) }, null);

        Assert.Empty(Store.QuerySampledTicks("net", 0, 10_000));
    }

    [Fact]
    public void ResolveAppKey_TreatsDifferentlyCasedNames_AsTheSameApp()
    {
        Store.Append(new[] { CpuTick(1000, ("Chrome.exe", 10)), CpuTick(1005, ("chrome.exe", 30)) }, null);

        var top = Store.QueryTopApps("cpu", 0, 10_000, 15);

        var app = Assert.Single(top);
        Assert.Equal(20, app.Avg); // both ticks resolved to one app row
    }

    [Fact]
    public void ResolveAppKey_KeepsTheFirstSeenCasing_OnALaterConflict()
    {
        Store.Append(new[] { CpuTick(1000, ("Chrome.exe", 10)) }, null);
        Store.Append(new[] { CpuTick(1005, ("chrome.exe", 30)) }, null);

        var app = Assert.Single(Store.QueryTopApps("cpu", 0, 10_000, 15));

        Assert.Equal("Chrome.exe", app.Name);
    }

    [Fact]
    public void QueryAppSeries_MatchesByName_CaseInsensitively()
    {
        Store.Append(new[] { CpuTick(1000, ("Chrome.exe", 10)) }, null);

        var points = Store.QueryAppSeries("cpu", "CHROME.EXE", 0, 10_000);

        Assert.Single(points);
    }

    [Fact]
    public void QueryTopApps_ReturnsEmpty_ForAnUnrecognizedMetric()
    {
        Store.Append(new[] { CpuTick(1000, ("app.exe", 10)) }, null);

        var result = Store.QueryTopApps("net", 0, 10_000, 15);

        Assert.Empty(result);
    }

    [Fact]
    public void QueryAppSeries_ReturnsEmpty_ForAnUnknownApp()
    {
        Store.Append(new[] { CpuTick(1000, ("app.exe", 10)) }, null);

        var result = Store.QueryAppSeries("cpu", "never-seen.exe", 0, 10_000);

        Assert.Empty(result);
    }

    [Fact]
    public void Append_RoundTripsGpuAppRows_KeyedByTheScalarGpuSurrogate()
    {
        // The scalar Append resolves gpu_series first, matching MetricsSampler.Flush's
        // call order (scalar store append, then app store append).
        var scalarSample = new MetricSample(1000, null, null, null, null, null,
            new[] { new GpuReading("gpu-0", "RTX 5080", "", 50, 60) }, Array.Empty<FanReading>());
        AppendScalar(scalarSample);

        var gpuTick = new AppUsageTick(1000, new[]
        {
            new AppMetricSample("gpu:gpu-0", new[] { new AppUsagePoint("game.exe", 40, 2048) }),
        });
        Store.Append(new[] { gpuTick }, null);

        var top = Store.QueryTopApps("gpu:gpu-0", 0, 10_000, 15);
        var app = Assert.Single(top);
        Assert.Equal("game.exe", app.Name);
        Assert.Equal(40, app.Avg);

        var points = Store.QueryAppSeries("gpu:gpu-0", "game.exe", 0, 10_000);
        var point = Assert.Single(points);
        Assert.Equal(40, point.Value);
        Assert.Equal(2048, point.VramMb);
    }

    [Fact]
    public void Append_SkipsGpuAppRows_WhenTheGpuIdHasNoScalarRow()
    {
        var gpuTick = new AppUsageTick(1000, new[]
        {
            new AppMetricSample("gpu:never-scalar-recorded", new[] { new AppUsagePoint("game.exe", 40, 2048) }),
        });

        Store.Append(new[] { gpuTick }, null);

        Assert.Empty(Store.QueryTopApps("gpu:never-scalar-recorded", 0, 10_000, 15));
    }

    [Fact]
    public void Append_WithPruneCutoff_ReusesAFreshKey_ForAnAppPrunedEntirelyOutOfTheWindow()
    {
        // The app row (and its cached key/identity) for "gone.exe" must
        // resolve to nothing once no sample of it survives the prune -
        // otherwise a later reappearance of the same name would read as if
        // it had been there the whole time.
        Store.Append(new[] { CpuTick(1000, ("gone.exe", 10)) }, null);
        Store.Append(Array.Empty<AppUsageTick>(), pruneCutoffSec: 5000);

        Store.Append(new[] { CpuTick(6000, ("gone.exe", 20)) }, null);

        var top = Store.QueryTopApps("cpu", 0, 10_000, 15);
        var app = Assert.Single(top);
        Assert.Equal(20, app.Avg);
    }

    [Fact]
    public void Append_WithPruneCutoff_KeepsAnAppRow_WhileAnyMetricStillHasSamples()
    {
        Store.Append(new[] { CpuTick(1000, ("kept.exe", 10)), CpuTick(9000, ("kept.exe", 30)) }, null);

        Store.Append(Array.Empty<AppUsageTick>(), pruneCutoffSec: 5000);

        var top = Store.QueryTopApps("cpu", 0, 10_000, 15);
        Assert.Single(top);
    }

    [Fact]
    public void Append_WithPruneCutoff_KeepsAnAppRow_ForAnAppSeenOnlyInVram()
    {
        // "vram-only.exe" never appears in cpu/mem/gpu, only in vram. The
        // prune must treat vram as a "still has samples" metric too, or this
        // app is dropped from QueryTopApps regardless of its still-present
        // vram row.
        var scalarSample = new MetricSample(1000, null, null, null, null, null,
            new[] { new GpuReading("gpu-0", "RTX 5080", "", 50, 60) }, Array.Empty<FanReading>());
        AppendScalar(scalarSample);
        Store.Append(new[] { VramTick(9000, "gpu-0", ("vram-only.exe", 1000)) }, null);

        Store.Append(Array.Empty<AppUsageTick>(), pruneCutoffSec: 5000);

        var top = Store.QueryTopApps("vram:gpu-0", 0, 10_000, 15);
        var app = Assert.Single(top);
        Assert.Equal("vram-only.exe", app.Name);
        Assert.Equal(1000, app.Avg);
    }

    [Fact]
    public void Append_WithPruneCutoff_DeletesOlderAppRows()
    {
        Store.Append(new[] { CpuTick(1000, ("app.exe", 10)), CpuTick(5000, ("app.exe", 20)) }, null);

        Store.Append(Array.Empty<AppUsageTick>(), pruneCutoffSec: 3000);

        var points = Store.QueryAppSeries("cpu", "app.exe", 0, 10_000);
        var point = Assert.Single(points);
        Assert.Equal(5000, point.TsSec);
    }

    [Fact]
    public void Append_EmptyList_WithNoCutoff_IsANoOp()
    {
        Store.Append(Array.Empty<AppUsageTick>(), null);

        Assert.Empty(Store.QueryTopApps("cpu", 0, long.MaxValue, 15));
    }

    [Fact]
    public void QueryFirstSeen_ReturnsNull_ForAnAppNeverRecorded()
    {
        Store.Append(new[] { CpuTick(1000, ("app.exe", 10)) }, null);

        Assert.Null(Store.QueryFirstSeen("never-seen.exe"));
    }

    [Fact]
    public void QueryFirstSeen_ReturnsTheEarliestTs_AcrossMultipleTicks()
    {
        Store.Append(new[] { CpuTick(5000, ("app.exe", 10)), CpuTick(1000, ("app.exe", 20)) }, null);

        Assert.Equal(1000, Store.QueryFirstSeen("app.exe"));
    }

    [Fact]
    public void QueryFirstSeen_TakesTheEarliestAcrossCpuAndGpuTables()
    {
        var scalarSample = new MetricSample(1000, null, null, null, null, null,
            new[] { new GpuReading("gpu-0", "RTX 5080", "", 50, 60) }, Array.Empty<FanReading>());
        AppendScalar(scalarSample);
        Store.Append(new[] { CpuTick(9000, ("app.exe", 10)) }, null);
        Store.Append(new[]
        {
            new AppUsageTick(2000, new[] { new AppMetricSample("gpu:gpu-0", new[] { new AppUsagePoint("app.exe", 40, 2048) }) }),
        }, null);

        Assert.Equal(2000, Store.QueryFirstSeen("app.exe"));
    }

    [Fact]
    public void QueryFirstSeen_MatchesByName_CaseInsensitively()
    {
        Store.Append(new[] { CpuTick(1000, ("Chrome.exe", 10)) }, null);

        Assert.Equal(1000, Store.QueryFirstSeen("CHROME.EXE"));
    }

    [Fact]
    public void QueryTopApps_BareGpu_SumsAnAppsValueAcrossTwoAdapters()
    {
        AppendScalar(ScalarGpuSample(1000, "gpu-0", "RTX 5080"), ScalarGpuSample(1000, "gpu-1", "RX 7800"));
        Store.Append(new[] { MultiAdapterGpuTick(1000, "gpu-0", ("game.exe", 30, null), "gpu-1", ("game.exe", 10, null)) }, null);

        var top = Store.QueryTopApps("gpu", 0, 10_000, 15);

        var app = Assert.Single(top);
        Assert.Equal("game.exe", app.Name);
        Assert.Equal(40, app.Avg);
        Assert.Equal(40, app.Max);
    }

    [Fact]
    public void QueryTopApps_BareGpu_MaxReflectsTheCombinedAdapterPeakInASingleTick()
    {
        AppendScalar(ScalarGpuSample(1000, "gpu-0", "RTX 5080"), ScalarGpuSample(1000, "gpu-1", "RX 7800"));
        // Tick 1: 30 + 10 = 40 combined. Tick 2: 5 + 5 = 10 combined. No
        // single row ever reaches 40, so a per-row MAX (the pre-fix bug)
        // would report 30, not the combined-adapter peak of 40.
        Store.Append(new[]
        {
            MultiAdapterGpuTick(1000, "gpu-0", ("game.exe", 30, null), "gpu-1", ("game.exe", 10, null)),
            MultiAdapterGpuTick(1005, "gpu-0", ("game.exe", 5, null), "gpu-1", ("game.exe", 5, null)),
        }, null);

        var app = Assert.Single(Store.QueryTopApps("gpu", 0, 10_000, 15));

        Assert.Equal(25, app.Avg); // (40 + 10) / 2 ticks
        Assert.Equal(40, app.Max);
    }

    [Fact]
    public void QueryTopApps_SpecificGpuId_StillFiltersToThatAdapterOnly()
    {
        AppendScalar(ScalarGpuSample(1000, "gpu-0", "RTX 5080"), ScalarGpuSample(1000, "gpu-1", "RX 7800"));
        Store.Append(new[] { MultiAdapterGpuTick(1000, "gpu-0", ("game.exe", 30, null), "gpu-1", ("game.exe", 10, null)) }, null);

        var app = Assert.Single(Store.QueryTopApps("gpu:gpu-0", 0, 10_000, 15));

        Assert.Equal(30, app.Avg);
    }

    [Fact]
    public void QueryAppSeries_BareGpu_SumsPerTickAcrossAdapters_IncludingVram()
    {
        AppendScalar(ScalarGpuSample(1000, "gpu-0", "RTX 5080"), ScalarGpuSample(1000, "gpu-1", "RX 7800"));
        Store.Append(new[] { MultiAdapterGpuTick(1000, "gpu-0", ("game.exe", 30, 1000), "gpu-1", ("game.exe", 10, 500)) }, null);

        var point = Assert.Single(Store.QueryAppSeries("gpu", "game.exe", 0, 10_000));

        Assert.Equal(1000, point.TsSec);
        Assert.Equal(40, point.Value);
        Assert.Equal(1500, point.VramMb);
    }

    [Fact]
    public void QuerySampledTicks_BareGpu_CountsATick_WithActivityOnOnlyOneAdapter()
    {
        AppendScalar(ScalarGpuSample(1000, "gpu-0", "RTX 5080"));
        Store.Append(new[] { GpuTick(1000, "gpu-0", ("game.exe", 30, null)) }, null);

        var ticks = Store.QuerySampledTicks("gpu", 0, 10_000);

        Assert.Equal(new long[] { 1000 }, ticks);
    }

    [Fact]
    public void Append_RoundTripsVramAppRows_KeyedByTheScalarGpuSurrogate()
    {
        var scalarSample = new MetricSample(1000, null, null, null, null, null,
            new[] { new GpuReading("gpu-0", "RTX 5080", "", 50, 60) }, Array.Empty<FanReading>());
        AppendScalar(scalarSample);

        Store.Append(new[] { VramTick(1000, "gpu-0", ("game.exe", 2048)) }, null);

        var top = Store.QueryTopApps("vram:gpu-0", 0, 10_000, 15);
        var app = Assert.Single(top);
        Assert.Equal("game.exe", app.Name);
        Assert.Equal(2048, app.Avg);
        Assert.Equal(2048, app.Max);

        var points = Store.QueryAppSeries("vram:gpu-0", "game.exe", 0, 10_000);
        var point = Assert.Single(points);
        Assert.Equal(2048, point.Value);
        Assert.Null(point.VramMb);
    }

    [Fact]
    public void Append_SkipsVramAppRows_WhenTheGpuIdHasNoScalarRow()
    {
        Store.Append(new[] { VramTick(1000, "never-scalar-recorded", ("game.exe", 2048)) }, null);

        Assert.Empty(Store.QueryTopApps("vram:never-scalar-recorded", 0, 10_000, 15));
    }

    [Fact]
    public void QueryTopApps_BareVram_SumsAnAppsValueAcrossTwoAdapters()
    {
        AppendScalar(ScalarGpuSample(1000, "gpu-0", "RTX 5080"), ScalarGpuSample(1000, "gpu-1", "RX 7800"));
        Store.Append(new[] { MultiAdapterVramTick(1000, "gpu-0", ("game.exe", 1500), "gpu-1", ("game.exe", 500)) }, null);

        var top = Store.QueryTopApps("vram", 0, 10_000, 15);

        var app = Assert.Single(top);
        Assert.Equal("game.exe", app.Name);
        Assert.Equal(2000, app.Avg);
        Assert.Equal(2000, app.Max);
    }

    [Fact]
    public void QueryTopApps_SpecificVramId_StillFiltersToThatAdapterOnly()
    {
        AppendScalar(ScalarGpuSample(1000, "gpu-0", "RTX 5080"), ScalarGpuSample(1000, "gpu-1", "RX 7800"));
        Store.Append(new[] { MultiAdapterVramTick(1000, "gpu-0", ("game.exe", 1500), "gpu-1", ("game.exe", 500)) }, null);

        var app = Assert.Single(Store.QueryTopApps("vram:gpu-0", 0, 10_000, 15));

        Assert.Equal(1500, app.Avg);
    }

    [Fact]
    public void QueryAppSeries_BareVram_SumsPerTickAcrossAdapters()
    {
        AppendScalar(ScalarGpuSample(1000, "gpu-0", "RTX 5080"), ScalarGpuSample(1000, "gpu-1", "RX 7800"));
        Store.Append(new[] { MultiAdapterVramTick(1000, "gpu-0", ("game.exe", 1500), "gpu-1", ("game.exe", 500)) }, null);

        var point = Assert.Single(Store.QueryAppSeries("vram", "game.exe", 0, 10_000));

        Assert.Equal(1000, point.TsSec);
        Assert.Equal(2000, point.Value);
    }

    [Fact]
    public void QuerySampledTicks_BareVram_CountsATick_WithActivityOnOnlyOneAdapter()
    {
        AppendScalar(ScalarGpuSample(1000, "gpu-0", "RTX 5080"));
        Store.Append(new[] { VramTick(1000, "gpu-0", ("game.exe", 1500)) }, null);

        var ticks = Store.QuerySampledTicks("vram", 0, 10_000);

        Assert.Equal(new long[] { 1000 }, ticks);
    }

    [Fact]
    public void QueryTopApps_RanksVram_ByWindowAverage_NotOwnRowCount()
    {
        AppendScalar(ScalarGpuSample(1000, "gpu-0", "RTX 5080"));
        Store.Append(new[]
        {
            VramTick(1000, "gpu-0", ("spike", 4000), ("sustained", 1000)),
            VramTick(1005, "gpu-0", ("sustained", 1000)),
        }, null);

        var top = Store.QueryTopApps("vram:gpu-0", 0, 10_000, 15);

        var spike = top.Single(a => a.Name == "spike");
        Assert.Equal(2000, spike.Avg); // 4000 / 2 sampled ticks, not 4000 / 1 own row
        var sustained = top.Single(a => a.Name == "sustained");
        Assert.Equal(1000, sustained.Avg);
    }

    [Fact]
    public void QueryFirstSeen_TakesTheEarliestAcrossCpuAndVramTables()
    {
        var scalarSample = new MetricSample(1000, null, null, null, null, null,
            new[] { new GpuReading("gpu-0", "RTX 5080", "", 50, 60) }, Array.Empty<FanReading>());
        AppendScalar(scalarSample);
        Store.Append(new[] { CpuTick(9000, ("app.exe", 10)) }, null);
        Store.Append(new[] { VramTick(2000, "gpu-0", ("app.exe", 1000)) }, null);

        Assert.Equal(2000, Store.QueryFirstSeen("app.exe"));
    }

    [Fact]
    public void Append_WithPruneCutoff_DeletesOlderVramRows()
    {
        var scalarSample = new MetricSample(1000, null, null, null, null, null,
            new[] { new GpuReading("gpu-0", "RTX 5080", "", 50, 60) }, Array.Empty<FanReading>());
        AppendScalar(scalarSample);
        Store.Append(new[] { VramTick(1000, "gpu-0", ("app.exe", 1000)), VramTick(5000, "gpu-0", ("app.exe", 2000)) }, null);

        Store.Append(Array.Empty<AppUsageTick>(), pruneCutoffSec: 3000);

        var points = Store.QueryAppSeries("vram:gpu-0", "app.exe", 0, 10_000);
        var point = Assert.Single(points);
        Assert.Equal(5000, point.TsSec);
    }

    [Fact]
    public void AppKey_stability_across_reopen()
    {
        Store.Append(new[] { CpuTick(1000, ("app.exe", 10)) }, null);
        Reopen();

        Store.Append(new[] { CpuTick(2000, ("app.exe", 20)) }, null);

        var points = Store.QueryAppSeries("cpu", "app.exe", 0, 10_000);
        Assert.Equal(2, points.Count);
    }
}

public sealed class SqliteAppUsageHistorySpecTests : AppUsageHistorySpec
{
    protected override IAppUsageHistoryStore CreateStore(string dir) =>
        new SqliteMetricsHistoryStore(Path.Combine(dir, "metrics.db"));
}

public sealed class BinaryAppUsageHistorySpecTests : AppUsageHistorySpec
{
    protected override IAppUsageHistoryStore CreateStore(string dir) =>
        new BinaryMetricsHistoryStore(dir);
}
