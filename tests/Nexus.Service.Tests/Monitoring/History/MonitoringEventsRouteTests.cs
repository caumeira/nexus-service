using Nexus.Service.Routes;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History;

/// <summary>
/// GET/POST /monitoring/events validation and normalization: pure, directly
/// unit-tested per the MonitoringHistoryRoutes precedent (ParseSeriesFilter,
/// BuildHistoryResponse) rather than a full HTTP harness.
/// </summary>
public class MonitoringEventsRouteTests
{
    [Fact]
    public void NormalizeCustomEventLabel_TrimsWhitespace()
    {
        var error = MonitoringHistoryRoutes.NormalizeCustomEventLabel("  hello world  ", out var label);

        Assert.Null(error);
        Assert.Equal("hello world", label);
    }

    [Fact]
    public void NormalizeCustomEventLabel_RejectsAnEmptyLabel_AfterTrimming()
    {
        var error = MonitoringHistoryRoutes.NormalizeCustomEventLabel("   ", out _);

        Assert.NotNull(error);
    }

    [Fact]
    public void NormalizeCustomEventLabel_RejectsANullLabel()
    {
        var error = MonitoringHistoryRoutes.NormalizeCustomEventLabel(null, out _);

        Assert.NotNull(error);
    }

    [Fact]
    public void NormalizeCustomEventLabel_TruncatesTo120Characters()
    {
        var longLabel = new string('a', 200);

        var error = MonitoringHistoryRoutes.NormalizeCustomEventLabel(longLabel, out var label);

        Assert.Null(error);
        Assert.Equal(120, label.Length);
    }

    [Fact]
    public void NormalizeCustomEventLabel_KeepsAShortLabel_Unchanged()
    {
        var error = MonitoringHistoryRoutes.NormalizeCustomEventLabel("short", out var label);

        Assert.Null(error);
        Assert.Equal("short", label);
    }

    [Fact]
    public void ClampFutureEventTime_LeavesAPastOrPresentTimeUnchanged()
    {
        var now = 10_000L;

        Assert.Equal(9_000L, MonitoringHistoryRoutes.ClampFutureEventTime(9_000L, now));
        Assert.Equal(now, MonitoringHistoryRoutes.ClampFutureEventTime(now, now));
    }

    [Fact]
    public void ClampFutureEventTime_LeavesATimeWithinTheGraceWindowUnchanged()
    {
        var now = 10_000L;

        Assert.Equal(now + 60_000L, MonitoringHistoryRoutes.ClampFutureEventTime(now + 60_000L, now));
    }

    [Fact]
    public void ClampFutureEventTime_ClampsATimeBeyondTheGraceWindow_ToNow()
    {
        var now = 10_000L;

        Assert.Equal(now, MonitoringHistoryRoutes.ClampFutureEventTime(now + 60_001L, now));
    }
}
