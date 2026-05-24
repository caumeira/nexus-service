using Nexus.Service.Models.Activity;

namespace Nexus.Service.Tests;

/// <summary>
/// Sanity tests on the ScreenTime DTOs themselves. The provider/store behaviour
/// lives in SqliteScreenTimeStoreTests and InMemoryScreenTimeStoreTests.
/// </summary>
public class ScreenTimeTrackingTests
{
    [Fact]
    public void AppUsage_SortsByDescendingTotalMs()
    {
        var usage = new List<AppUsage>
        {
            new() { Name = "Slack", TotalMs = 10000 },
            new() { Name = "Chrome", TotalMs = 60000 },
            new() { Name = "Terminal", TotalMs = 30000 },
        };

        var sorted = usage.OrderByDescending(a => a.TotalMs).ToList();

        Assert.Equal("Chrome", sorted[0].Name);
        Assert.Equal("Terminal", sorted[1].Name);
        Assert.Equal("Slack", sorted[2].Name);
    }

    [Fact]
    public void FocusSession_ReflectsCurrentApp()
    {
        var session = new FocusSession
        {
            Id = "42",
            Name = "Visual Studio Code",
            Today = new Duration { Total = 120000, Minutes = 2 },
        };

        Assert.Equal("42", session.Id);
        Assert.Equal("Visual Studio Code", session.Name);
        Assert.Equal(120000, session.Today.Total);
    }

    [Fact]
    public void DayBreakdown_DefaultsAreSafe()
    {
        var d = new DayBreakdown();

        Assert.Equal(0, d.TotalMs);
        Assert.Equal(0, d.Pickups);
        Assert.Empty(d.Apps);
        Assert.Empty(d.HourlyMs);
    }
}
