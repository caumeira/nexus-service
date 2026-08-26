using System.Collections.Generic;
using Nexus.Service.Conflicts;
using Nexus.Service.Models.Conflicts;
using Xunit;

namespace Nexus.Service.Tests.Conflicts;

/// <summary>
/// The watcher only rebuilds and rebroadcasts when this key changes, so the key
/// decides what a client can ever observe. nexus-web's End task button reads a
/// pid change as "the app came back", which is unreachable if a respawn does
/// not move the key.
/// </summary>
public class ConflictWatcherChangeKeyTests
{
    private static DetectedConflict C(string id, int pid) => new() { Id = id, Pid = pid };

    [Fact]
    public void ARespawnUnderANewPidChangesTheKey()
    {
        var before = ConflictWatcher.ChangeKey(new List<DetectedConflict> { C("signalrgb", 100) });
        var after = ConflictWatcher.ChangeKey(new List<DetectedConflict> { C("signalrgb", 412) });

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void AnUnchangedSetKeepsTheSameKey()
    {
        var a = ConflictWatcher.ChangeKey(new List<DetectedConflict> { C("signalrgb", 100), C("icue", 7) });
        var b = ConflictWatcher.ChangeKey(new List<DetectedConflict> { C("signalrgb", 100), C("icue", 7) });

        Assert.Equal(a, b);
    }

    [Fact]
    public void DetectionOrderDoesNotChangeTheKey()
    {
        var a = ConflictWatcher.ChangeKey(new List<DetectedConflict> { C("icue", 7), C("signalrgb", 100) });
        var b = ConflictWatcher.ChangeKey(new List<DetectedConflict> { C("signalrgb", 100), C("icue", 7) });

        Assert.Equal(a, b);
    }

    [Fact]
    public void AnAppLeavingOrArrivingChangesTheKey()
    {
        var one = ConflictWatcher.ChangeKey(new List<DetectedConflict> { C("icue", 7) });
        var two = ConflictWatcher.ChangeKey(new List<DetectedConflict> { C("icue", 7), C("signalrgb", 100) });

        Assert.NotEqual(one, two);
        Assert.Equal(ConflictWatcher.ChangeKey(new List<DetectedConflict>()), ConflictWatcher.ChangeKey(new List<DetectedConflict>()));
    }
}
