using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Conflicts;
using Nexus.Service.Models.Conflicts;
using Xunit;

namespace Nexus.Service.Tests.Conflicts;

/// <summary>
/// The open/close diff behind the [conflicts] log lines. Keyed on app id rather
/// than ChangeKey's id:pid, so a restart between scans is not reported as the app
/// having closed and reopened.
/// </summary>
public class ConflictWatcherTransitionTests
{
    private static DetectedConflict App(string id, int pid = 100) =>
        new() { Id = id, DisplayName = id.ToUpperInvariant(), Pid = pid };

    [Fact]
    public void AnArrivingAppIsReportedOpened()
    {
        var lines = ConflictWatcher.TransitionLines(Array.Empty<string>(), new[] { App("icue", 42) });
        var line = Assert.Single(lines);
        Assert.Equal("icue", line.Id);
        Assert.Contains("opened", line.Message);
        Assert.Contains("pid=42", line.Message);
    }

    [Fact]
    public void ADepartedAppIsReportedClosed()
    {
        var lines = ConflictWatcher.TransitionLines(new[] { "icue" }, Array.Empty<DetectedConflict>());
        var line = Assert.Single(lines);
        Assert.Equal("icue", line.Id);
        Assert.Contains("closed", line.Message);
    }

    [Fact]
    public void AClosedAppIsNamedFromTheCatalog()
    {
        // The DetectedConflict carrying the display name is gone by then, so the
        // line has to resolve the name from the id.
        var lines = ConflictWatcher.TransitionLines(new[] { "icue" }, Array.Empty<DetectedConflict>());
        Assert.Contains("Corsair iCUE", Assert.Single(lines).Message);
    }

    [Fact]
    public void AnUnchangedSetProducesNothing()
    {
        var lines = ConflictWatcher.TransitionLines(new[] { "icue" }, new[] { App("icue") });
        Assert.Empty(lines);
    }

    [Fact]
    public void ARestartUnderANewPidIsNotAClosedOpenedPair()
    {
        // ChangeKey would see this as changed, because it keys on id:pid.
        var lines = ConflictWatcher.TransitionLines(new[] { "icue" }, new[] { App("icue", 999) });
        Assert.Empty(lines);
        Assert.NotEqual(
            ConflictWatcher.ChangeKey(new[] { App("icue", 1) }),
            ConflictWatcher.ChangeKey(new[] { App("icue", 999) }));
    }

    [Fact]
    public void OneScanCanReportBothDirections()
    {
        var lines = ConflictWatcher.TransitionLines(new[] { "icue" }, new[] { App("signalrgb") });
        Assert.Equal(2, lines.Count);
        Assert.Contains(lines, l => l.Id == "signalrgb" && l.Message.Contains("opened"));
        Assert.Contains(lines, l => l.Id == "icue" && l.Message.Contains("closed"));
    }

    [Fact]
    public void SortedIdsIsCaseInsensitivelyOrdered()
    {
        var ids = ConflictWatcher.SortedIds(new[] { App("signalrgb"), App("icue") });
        Assert.Equal(new[] { "icue", "signalrgb" }, ids);
    }
}
