using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Monitoring.History;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History;

public class MetricsDecimationTests
{
    [Theory]
    [InlineData(600, 600, 1)]   // 1:1 -> narrowest step
    [InlineData(1200, 600, 2)]  // needs 2s/point -> ladder picks 2
    [InlineData(3000, 600, 5)]  // needs 5s/point -> ladder picks 5
    [InlineData(7 * 86_400, 600, 1800)] // 7 days at 600 points -> 1008s/point -> next ladder step is 1800
    public void StepSecondsFor_picks_the_narrowest_ladder_step_covering_the_window(long windowSeconds, int maxPoints, int expected)
    {
        Assert.Equal(expected, MetricsDecimation.StepSecondsFor(windowSeconds, maxPoints));
    }

    [Fact]
    public void StepSecondsFor_falls_back_to_the_widest_step_when_nothing_covers_the_window()
    {
        Assert.Equal(3600, MetricsDecimation.StepSecondsFor(365L * 86_400, 1));
    }

    [Fact]
    public void StepSecondsFor_clamps_a_non_positive_maxPoints_to_one()
    {
        Assert.Equal(MetricsDecimation.StepSecondsFor(10, 1), MetricsDecimation.StepSecondsFor(10, 0));
    }

    private static MetricSamplePoint P(long ts, double? v) => new(ts, v);

    [Fact]
    public void Decimate_averages_and_maxes_points_within_the_same_slot()
    {
        var points = new[] { P(0, 10), P(1, 20), P(2, 30) };

        var result = MetricsDecimation.Decimate(points, 0, 10, stepSeconds: 5);

        var point = Assert.Single(result);
        Assert.Equal(0, point.T);
        Assert.Equal(20, point.Avg);
        Assert.Equal(30, point.Max);
    }

    [Fact]
    public void Decimate_splits_points_across_slot_boundaries()
    {
        var points = new[] { P(0, 10), P(5, 20), P(9, 30), P(10, 40) };

        var result = MetricsDecimation.Decimate(points, 0, 19, stepSeconds: 10);

        Assert.Equal(2, result.Count);
        Assert.Equal(0, result[0].T);
        Assert.Equal(20, result[0].Avg); // avg(10,20,30)
        Assert.Equal(30, result[0].Max);
        Assert.Equal(10, result[1].T);
        Assert.Equal(40, result[1].Avg);
        Assert.Equal(40, result[1].Max);
    }

    [Fact]
    public void Decimate_slot_alignment_uses_floor_division()
    {
        var points = new[] { P(3, 10), P(7, 20), P(12, 30) };

        var result = MetricsDecimation.Decimate(points, 0, 20, stepSeconds: 10);

        Assert.Equal(2, result.Count);
        Assert.Equal(0, result[0].T);   // slot for ts 3 and 7 -> floor(3/10)*10 = 0
        Assert.Equal(15.0, result[0].Avg);
        Assert.Equal(10, result[1].T);  // slot for ts 12 -> floor(12/10)*10 = 10
        Assert.Equal(30, result[1].Avg);
    }

    [Fact]
    public void Decimate_omits_a_slot_with_no_samples_instead_of_interpolating()
    {
        var points = new[] { P(0, 10), P(30, 20) }; // gap in the middle slot

        var result = MetricsDecimation.Decimate(points, 0, 29, stepSeconds: 10);

        var point = Assert.Single(result); // only the slot containing ts 0
        Assert.Equal(0, point.T);
    }

    [Fact]
    public void Decimate_treats_a_null_value_as_no_data_for_that_tick()
    {
        var points = new[] { P(0, 10), P(1, null), P(2, 30) };

        var result = MetricsDecimation.Decimate(points, 0, 10, stepSeconds: 5);

        var point = Assert.Single(result);
        Assert.Equal(20, point.Avg); // only ts 0 and 2 contribute
    }

    [Fact]
    public void Decimate_excludes_points_outside_the_requested_range()
    {
        var points = new[] { P(0, 10), P(5, 20), P(100, 30) };

        var result = MetricsDecimation.Decimate(points, 0, 10, stepSeconds: 5);

        Assert.All(result, p => Assert.True(p.T <= 10));
        Assert.DoesNotContain(result, p => p.Avg == 30);
    }

    [Fact]
    public void Decimate_returns_empty_for_no_points()
    {
        Assert.Empty(MetricsDecimation.Decimate(Enumerable.Empty<MetricSamplePoint>(), 0, 10, stepSeconds: 5));
    }

    [Fact]
    public void Decimate_returns_empty_when_all_values_are_null()
    {
        var points = new[] { P(0, null), P(1, null) };

        Assert.Empty(MetricsDecimation.Decimate(points, 0, 10, stepSeconds: 5));
    }
}
