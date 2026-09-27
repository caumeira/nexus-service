using Nexus.Service.Platform.Displays;

namespace Nexus.Service.Tests;

public class TouchRoutingStatusTests
{
    [Fact]
    public void Permission_needed_flags_only_the_reported_display()
    {
        var status = new TouchRoutingStatus();
        Assert.Equal(new[] { "edge" }, status.Report("edge", TouchRoutingStatus.PermissionNeeded));
        Assert.Equal(TouchRoutingStatus.PermissionWarning, status.WarningFor("edge"));
        Assert.Null(status.WarningFor("other"));
        Assert.Null(status.WarningFor(null));
    }

    [Fact]
    public void Active_and_idle_clear_the_warning_and_report_the_change()
    {
        var status = new TouchRoutingStatus();
        status.Report("edge", TouchRoutingStatus.PermissionNeeded);
        Assert.Equal(new[] { "edge" }, status.Report("edge", TouchRoutingStatus.Active));
        Assert.Null(status.WarningFor("edge"));

        status.Report("edge", TouchRoutingStatus.PermissionNeeded);
        Assert.Equal(new[] { "edge" }, status.Report("", TouchRoutingStatus.Idle));
        Assert.Null(status.WarningFor("edge"));
    }

    [Fact]
    public void Unchanged_warning_reports_nothing_and_a_moved_one_reports_both_displays()
    {
        var status = new TouchRoutingStatus();
        Assert.Empty(status.Report("edge", TouchRoutingStatus.Active));
        status.Report("edge", TouchRoutingStatus.PermissionNeeded);
        Assert.Empty(status.Report("edge", TouchRoutingStatus.PermissionNeeded));
        Assert.Equal(new[] { "edge", "other" }, status.Report("other", TouchRoutingStatus.PermissionNeeded));
    }

    [Theory]
    [InlineData("active", true)]
    [InlineData("permission-needed", true)]
    [InlineData("idle", true)]
    [InlineData("", false)]
    [InlineData("Active", false)]
    [InlineData(null, false)]
    public void Only_the_three_helper_states_are_valid(string? state, bool valid)
        => Assert.Equal(valid, TouchRoutingStatus.IsValidState(state));
}
