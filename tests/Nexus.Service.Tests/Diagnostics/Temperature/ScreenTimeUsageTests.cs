using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Diagnostics.Temperature;
using Nexus.Service.Models.Activity;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics.Temperature;

public class ScreenTimeUsageTests
{
    private const long Min = 60_000L;
    private const long Slot = 5 * Min;
    private static readonly long T0 =
        new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static FocusSessionRow S(string app, long startMin, long endMin, string? path = null)
        => new(app, path, T0 + startMin * Min, T0 + endMin * Min);

    [Fact]
    public void Empty_ReturnsEmpty()
    {
        Assert.Empty(ScreenTimeUsage.Build(Array.Empty<FocusSessionRow>(), T0, T0 + 30 * Min, Slot));
    }

    [Fact]
    public void SingleSession_FillsEveryCoveredBucketWithThatApp()
    {
        var buckets = ScreenTimeUsage.Build(new[] { S("Chrome", 2, 27) }, T0, T0 + 30 * Min, Slot);

        Assert.Equal(6, buckets.Count);
        Assert.All(buckets, b => Assert.Equal("Chrome", Assert.Single(b.Apps).AppName));
        Assert.Equal(T0, buckets[0].StartUtcMs);        // keyed on the slot grid
        Assert.Equal(T0 + 25 * Min, buckets[5].StartUtcMs);
    }

    [Fact]
    public void MultipleAppsInABucket_AreSortedByTimeDescending()
    {
        // One 5-min slot: B holds it 3 min to A's 2 min.
        var buckets = ScreenTimeUsage.Build(new[] { S("A", 0, 2), S("B", 2, 5) }, T0, T0 + Slot, Slot);

        var apps = Assert.Single(buckets).Apps;
        Assert.Equal(2, apps.Count);
        Assert.Equal("B", apps[0].AppName);
        Assert.Equal(3 * Min, apps[0].Ms);
        Assert.Equal("A", apps[1].AppName);
        Assert.Equal(2 * Min, apps[1].Ms);
    }

    [Fact]
    public void IdleBucketsAreOmitted()
    {
        // A in slot 0 and slot 2, nothing in slot 1.
        var buckets = ScreenTimeUsage.Build(new[] { S("A", 0, 4), S("A", 11, 14) }, T0, T0 + 15 * Min, Slot);

        Assert.Equal(2, buckets.Count);
        Assert.Equal(T0, buckets[0].StartUtcMs);
        Assert.Equal(T0 + 10 * Min, buckets[1].StartUtcMs);
    }

    [Fact]
    public void SessionsOutsideTheWindowAreIgnored()
    {
        Assert.Empty(ScreenTimeUsage.Build(new[] { S("A", 0, 5) }, T0 + 10 * Min, T0 + 20 * Min, Slot));
    }

    [Fact]
    public void SameNameDifferentPath_AreDistinctApps()
    {
        var buckets = ScreenTimeUsage.Build(
            new[] { S("Terminal", 0, 3, path: "/a/term"), S("Terminal", 3, 5, path: "/b/term") },
            T0, T0 + Slot, Slot);

        var apps = Assert.Single(buckets).Apps;
        Assert.Equal(2, apps.Count);
        Assert.Equal(new[] { "/a/term", "/b/term" }, apps.Select(a => a.AppId).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void AppsPerBucketAreCapped()
    {
        var sessions = Enumerable.Range(0, 12)
            .Select(i => S($"app{i:D2}", 0, 5, path: $"/app{i:D2}"))
            .ToList();

        var apps = Assert.Single(ScreenTimeUsage.Build(sessions, T0, T0 + Slot, Slot)).Apps;

        Assert.Equal(8, apps.Count);
    }
}
