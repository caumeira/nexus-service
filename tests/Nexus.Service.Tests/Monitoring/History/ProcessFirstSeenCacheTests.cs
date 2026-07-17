using Nexus.Service.Monitoring.History;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History;

public class ProcessFirstSeenCacheTests
{
    private static AppUsageTick CpuTick(long ts, string name, double value) =>
        new(ts, new[] { new AppMetricSample("cpu", new[] { new AppUsagePoint(name, value, null) }) });

    [Fact]
    public void Resolve_ReturnsNull_WhenTheStoreHasNeverSeenTheApp()
    {
        var store = new InMemoryMetricsHistoryStore();
        var cache = new ProcessFirstSeenCache(store);

        Assert.Null(cache.Resolve("never-seen.exe"));
    }

    [Fact]
    public void Resolve_ConvertsTheStoresEpochSeconds_ToMilliseconds()
    {
        var store = new InMemoryMetricsHistoryStore();
        store.Append(new[] { CpuTick(1000, "app.exe", 10) }, null);
        var cache = new ProcessFirstSeenCache(store);

        Assert.Equal(1_000_000, cache.Resolve("app.exe"));
    }

    [Fact]
    public void Resolve_CachesTheFirstAnswer_AndDoesNotReflectALaterEarlierSample()
    {
        var store = new InMemoryMetricsHistoryStore();
        store.Append(new[] { CpuTick(5000, "app.exe", 10) }, null);
        var cache = new ProcessFirstSeenCache(store);
        var first = cache.Resolve("app.exe");

        // A later append with an even earlier ts would change the store's own
        // answer, but the cache must keep serving the first resolved value.
        store.Append(new[] { CpuTick(1000, "app.exe", 20) }, null);
        var second = cache.Resolve("app.exe");

        Assert.Equal(5_000_000, first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Resolve_DoesNotCache_WhenTheAppWasNotFoundYet()
    {
        var store = new InMemoryMetricsHistoryStore();
        var cache = new ProcessFirstSeenCache(store);
        Assert.Null(cache.Resolve("app.exe"));

        store.Append(new[] { CpuTick(1000, "app.exe", 10) }, null);

        Assert.Equal(1_000_000, cache.Resolve("app.exe"));
    }
}
