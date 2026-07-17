using System;
using System.IO;
using System.Linq;
using Nexus.Service.Monitoring.History;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History;

public class AppUsageHistoryStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private SqliteMetricsHistoryStore _store;

    public AppUsageHistoryStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-appusagehistory-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "metrics.db");
        _store = new SqliteMetricsHistoryStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static AppUsageTick CpuTick(long ts, params (string Name, double Value)[] apps) =>
        new(ts, new[] { new AppMetricSample("cpu", apps.Select(a => new AppUsagePoint(a.Name, a.Value, null)).ToList()) });

    [Fact]
    public void Append_then_QueryTopApps_RoundTripsCpuValues()
    {
        _store.Append(new[] { CpuTick(1000, ("app.exe", 42.3)) }, null);

        var top = _store.QueryTopApps("cpu", 0, 10_000, 15);

        var app = Assert.Single(top);
        Assert.Equal("app.exe", app.Name);
        Assert.Equal(42.3, app.Avg);
        Assert.Equal(42.3, app.Max);
    }

    [Fact]
    public void Append_then_QueryAppSeries_RoundTripsRawPoints_AscendingByTs()
    {
        _store.Append(new[] { CpuTick(2000, ("app.exe", 20)), CpuTick(1000, ("app.exe", 10)) }, null);

        var points = _store.QueryAppSeries("cpu", "app.exe", 0, 10_000);

        Assert.Equal(new long[] { 1000, 2000 }, points.Select(p => p.TsSec).ToArray());
        Assert.Equal(10, points[0].Value);
        Assert.Equal(20, points[1].Value);
    }

    [Fact]
    public void QueryTopApps_OrdersByAvgDescending_AndLimitsToMaxApps()
    {
        _store.Append(new[] { CpuTick(1000, ("low", 5), ("high", 90), ("mid", 40)) }, null);

        var top = _store.QueryTopApps("cpu", 0, 10_000, 2);

        Assert.Equal(new[] { "high", "mid" }, top.Select(a => a.Name).ToArray());
    }

    [Fact]
    public void QueryTopApps_ComputesWindowAverage_AcrossMultipleTicks()
    {
        _store.Append(new[] { CpuTick(1000, ("app.exe", 10)), CpuTick(1005, ("app.exe", 30)) }, null);

        var app = Assert.Single(_store.QueryTopApps("cpu", 0, 10_000, 15));

        Assert.Equal(20, app.Avg);
        Assert.Equal(30, app.Max);
    }

    [Fact]
    public void QueryTopApps_ReturnsEmpty_ForAnUnrecognizedMetric()
    {
        _store.Append(new[] { CpuTick(1000, ("app.exe", 10)) }, null);

        var result = _store.QueryTopApps("net", 0, 10_000, 15);

        Assert.Empty(result);
    }

    [Fact]
    public void QueryAppSeries_ReturnsEmpty_ForAnUnknownApp()
    {
        _store.Append(new[] { CpuTick(1000, ("app.exe", 10)) }, null);

        var result = _store.QueryAppSeries("cpu", "never-seen.exe", 0, 10_000);

        Assert.Empty(result);
    }

    [Fact]
    public void Append_RoundTripsGpuAppRows_KeyedByTheScalarGpuSurrogate()
    {
        // The scalar Append resolves gpu_series first, matching MetricsSampler.Flush's
        // call order (scalar store append, then app store append).
        var scalarSample = new MetricSample(1000, null, null, null, null, null,
            new[] { new GpuReading("gpu-0", "RTX 5080", "", 50, 60) }, Array.Empty<FanReading>());
        _store.Append(new[] { scalarSample }, null);

        var gpuTick = new AppUsageTick(1000, new[]
        {
            new AppMetricSample("gpu:gpu-0", new[] { new AppUsagePoint("game.exe", 40, 2048) }),
        });
        _store.Append(new[] { gpuTick }, null);

        var top = _store.QueryTopApps("gpu:gpu-0", 0, 10_000, 15);
        var app = Assert.Single(top);
        Assert.Equal("game.exe", app.Name);
        Assert.Equal(40, app.Avg);

        var points = _store.QueryAppSeries("gpu:gpu-0", "game.exe", 0, 10_000);
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

        _store.Append(new[] { gpuTick }, null);

        Assert.Empty(_store.QueryTopApps("gpu:never-scalar-recorded", 0, 10_000, 15));
    }

    [Fact]
    public void Append_WithPruneCutoff_DeletesOlderAppRows()
    {
        _store.Append(new[] { CpuTick(1000, ("app.exe", 10)), CpuTick(5000, ("app.exe", 20)) }, null);

        _store.Append(Array.Empty<AppUsageTick>(), pruneCutoffSec: 3000);

        var points = _store.QueryAppSeries("cpu", "app.exe", 0, 10_000);
        var point = Assert.Single(points);
        Assert.Equal(5000, point.TsSec);
    }

    [Fact]
    public void Append_EmptyList_WithNoCutoff_IsANoOp()
    {
        _store.Append(Array.Empty<AppUsageTick>(), null);

        Assert.Empty(_store.QueryTopApps("cpu", 0, long.MaxValue, 15));
    }

    [Fact]
    public void AppKey_stability_across_reopen()
    {
        _store.Append(new[] { CpuTick(1000, ("app.exe", 10)) }, null);
        _store.Dispose();

        _store = new SqliteMetricsHistoryStore(_dbPath);
        _store.Append(new[] { CpuTick(2000, ("app.exe", 20)) }, null);

        var points = _store.QueryAppSeries("cpu", "app.exe", 0, 10_000);
        Assert.Equal(2, points.Count);
    }
}
