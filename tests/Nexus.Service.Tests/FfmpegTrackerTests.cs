using Nexus.Service.Platform;
using Xunit;

namespace Nexus.Service.Tests;

public class FfmpegTrackerTests
{
    [Fact]
    public void CleanupOrphans_DoesNotThrow_WhenNoPidFile()
        => Assert.Null(Record.Exception(() => FfmpegTracker.CleanupOrphans()));

    [Fact]
    public void Track_And_Untrack_DoesNotThrow()
        => Assert.Null(Record.Exception(() =>
        {
            // A fake PID that definitely doesn't exist.
            FfmpegTracker.Track(999999);
            FfmpegTracker.Untrack(999999);
        }));
}
