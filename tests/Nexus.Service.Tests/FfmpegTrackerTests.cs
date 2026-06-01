using Nexus.Service.Platform;

namespace Nexus.Service.Tests;

public class FfmpegTrackerTests
{
    [Fact]
    public void CleanupOrphans_DoesNotThrow_WhenNoPidFile()
    {
        FfmpegTracker.CleanupOrphans();
    }

    [Fact]
    public void Track_And_Untrack_DoesNotThrow()
    {
        // Use a fake PID that definitely doesn't exist
        FfmpegTracker.Track(999999);
        FfmpegTracker.Untrack(999999);
    }
}
