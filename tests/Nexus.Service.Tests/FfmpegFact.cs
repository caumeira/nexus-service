using Nexus.Service.Platform;

namespace Nexus.Service.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> for tests that bake real media through the
/// bundled ffmpeg. Reported as skipped when the binary is absent rather than
/// returning early, so the suite's green never claims a bake it did not run.
/// </summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        if (FfmpegResolver.Path is null)
            Skip = "Requires the bundled ffmpeg.";
    }
}
