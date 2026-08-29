using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Conflicts;

/// <summary>
/// The startup shutdown terminates other vendors' apps, so its default state
/// is load-bearing: a user who never opens the modal must never have an app
/// killed on their behalf.
/// </summary>
public class ConflictStartupShutdownSettingsTests
{
    [Fact]
    public void TheStartupShutdownIsOffByDefault()
    {
        Assert.False(new UiSettings().AutoKillConflictsAtStartup);
    }

    [Fact]
    public void NoAppIsExcludedByDefault()
    {
        // Empty, never null: the client merges an absent field as "keep the
        // value the last profile had", so a profile that never excluded
        // anything must still send an empty list rather than nothing.
        Assert.Empty(new UiSettings().ConflictAutoKillExclusions);
    }
}
