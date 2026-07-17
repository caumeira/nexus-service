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
}
