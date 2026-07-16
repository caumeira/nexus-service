using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Diagnostics.Temperature;
using Nexus.Service.Routes;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics;

public class TemperatureRouteResponseTests
{
    private const long BucketMs = TemperatureRollup.BucketMinutes * 60_000L;
    private static readonly long T0Ms =
        new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static List<TemperatureBucketRow> SeriesOf(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new TemperatureBucketRow("cpu", "cpu", "CPU", T0Ms + i * BucketMs, 50, 55, 10))
            .ToList();

    [Fact]
    public void BuildTemperatureResponse_ReportsTheEffectiveTierWidth()
    {
        var rows = SeriesOf(24 * 12);

        var response = DiagnosticsHealthRoutes.BuildTemperatureResponse(rows, tierWidthMinutes: 30);

        Assert.True(response.Supported);
        Assert.Equal(30, response.BucketMinutes);
    }

    [Fact]
    public void BuildTemperatureResponse_IncludesRetentionDays()
    {
        var rows = SeriesOf(10);

        var response = DiagnosticsHealthRoutes.BuildTemperatureResponse(rows, tierWidthMinutes: 5);

        Assert.Equal(TemperatureRollup.RetentionDays, response.RetentionDays);
    }

    [Fact]
    public void BuildTemperatureResponse_MergesSeriesPointsToTheTierWidth()
    {
        // 14 days of raw 5-min buckets tiered at 60 min -> 336 points.
        var rows = SeriesOf(336 * 12);

        var response = DiagnosticsHealthRoutes.BuildTemperatureResponse(rows, tierWidthMinutes: 60);

        var series = Assert.Single(response.Series);
        Assert.Equal(336, series.Points.Count);
    }

    [Fact]
    public void BuildTemperatureResponse_RawTierLeavesOnePointPerBucket()
    {
        var rows = SeriesOf(24 * 12);

        var response = DiagnosticsHealthRoutes.BuildTemperatureResponse(rows, tierWidthMinutes: 5);

        var series = Assert.Single(response.Series);
        Assert.Equal(rows.Count, series.Points.Count);
    }

    [Fact]
    public void BuildTemperatureResponse_EpisodesCoverTheQueriedRows()
    {
        var rows = new List<TemperatureBucketRow>
        {
            new("cpu", "cpu", "CPU", T0Ms, 91, 91, 10),
            new("cpu", "cpu", "CPU", T0Ms + BucketMs, 92, 92, 10),
        };

        var response = DiagnosticsHealthRoutes.BuildTemperatureResponse(rows, tierWidthMinutes: 5);

        var episode = Assert.Single(response.Episodes);
        Assert.Equal("cpu", episode.ComponentId);
    }
}
