using System;
using System.Linq;
using Nexus.Service.Monitoring.History;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History;

/// <summary>
/// InMemoryMetricsHistoryStore's app-usage methods (the fallback path) must
/// count a "sampled tick" the same way SqliteMetricsHistoryStore does -
/// Apps.Count > 0 - so the same tick's contribution to expectedTicks does
/// not change depending on which store answers the query.
/// </summary>
public class InMemoryAppUsageHistoryStoreTests
{
    [Fact]
    public void QueryTopApps_DoesNotCountATick_WhoseMetricSampleHasNoApps()
    {
        var store = new InMemoryMetricsHistoryStore();
        store.Append(new[]
        {
            new AppUsageTick(1000, new[] { new AppMetricSample("cpu", new[] { new AppUsagePoint("app.exe", 40, null) }) }),
            new AppUsageTick(1005, new[] { new AppMetricSample("cpu", Array.Empty<AppUsagePoint>()) }),
        }, null);

        var app = Assert.Single(store.QueryTopApps("cpu", 0, 10_000, 15));

        // If the empty-apps tick counted as sampled, avg would be 40/2=20;
        // matching SqliteMetricsHistoryStore's row-based definition (that
        // tick persists zero rows, so it is not sampled) keeps it at 40/1.
        Assert.Equal(40, app.Avg);
    }

    [Fact]
    public void QuerySampledTicks_ExcludesATick_WhoseMetricSampleHasNoApps()
    {
        var store = new InMemoryMetricsHistoryStore();
        store.Append(new[]
        {
            new AppUsageTick(1000, new[] { new AppMetricSample("cpu", new[] { new AppUsagePoint("app.exe", 40, null) }) }),
            new AppUsageTick(1005, new[] { new AppMetricSample("cpu", Array.Empty<AppUsagePoint>()) }),
        }, null);

        var ticks = store.QuerySampledTicks("cpu", 0, 10_000);

        Assert.Equal(new long[] { 1000 }, ticks);
    }

    private static AppUsageTick MultiAdapterGpuTick(
        long ts, string gid0, (string Name, double Value, double? Vram) app0,
        string gid1, (string Name, double Value, double? Vram) app1) =>
        new(ts, new[]
        {
            new AppMetricSample($"gpu:{gid0}", new[] { new AppUsagePoint(app0.Name, app0.Value, app0.Vram) }),
            new AppMetricSample($"gpu:{gid1}", new[] { new AppUsagePoint(app1.Name, app1.Value, app1.Vram) }),
        });

    [Fact]
    public void QueryTopApps_BareGpu_SumsAnAppsValueAcrossTwoAdapters()
    {
        var store = new InMemoryMetricsHistoryStore();
        store.Append(new[] { MultiAdapterGpuTick(1000, "gpu-0", ("game.exe", 30, null), "gpu-1", ("game.exe", 10, null)) }, null);

        var app = Assert.Single(store.QueryTopApps("gpu", 0, 10_000, 15));

        Assert.Equal("game.exe", app.Name);
        Assert.Equal(40, app.Avg);
        Assert.Equal(40, app.Max);
    }

    [Fact]
    public void QueryTopApps_SpecificGpuId_StillFiltersToThatAdapterOnly()
    {
        var store = new InMemoryMetricsHistoryStore();
        store.Append(new[] { MultiAdapterGpuTick(1000, "gpu-0", ("game.exe", 30, null), "gpu-1", ("game.exe", 10, null)) }, null);

        var app = Assert.Single(store.QueryTopApps("gpu:gpu-0", 0, 10_000, 15));

        Assert.Equal(30, app.Avg);
    }

    [Fact]
    public void QueryAppSeries_BareGpu_SumsPerTickAcrossAdapters_IncludingVram()
    {
        var store = new InMemoryMetricsHistoryStore();
        store.Append(new[] { MultiAdapterGpuTick(1000, "gpu-0", ("game.exe", 30, 1000), "gpu-1", ("game.exe", 10, 500)) }, null);

        var point = Assert.Single(store.QueryAppSeries("gpu", "game.exe", 0, 10_000));

        Assert.Equal(1000, point.TsSec);
        Assert.Equal(40, point.Value);
        Assert.Equal(1500, point.VramMb);
    }

    [Fact]
    public void QuerySampledTicks_BareGpu_CountsATick_WithActivityOnOnlyOneAdapter()
    {
        var store = new InMemoryMetricsHistoryStore();
        store.Append(new[]
        {
            new AppUsageTick(1000, new[] { new AppMetricSample("gpu:gpu-0", new[] { new AppUsagePoint("game.exe", 30, null) }) }),
        }, null);

        var ticks = store.QuerySampledTicks("gpu", 0, 10_000);

        Assert.Equal(new long[] { 1000 }, ticks);
    }
}
