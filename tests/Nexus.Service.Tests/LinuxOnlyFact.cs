namespace Nexus.Service.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that only runs on Linux, for tests exercising
/// /proc and /sys reads. Reported as skipped elsewhere so the suite's green
/// never claims a verification that did not happen.
/// </summary>
public sealed class LinuxOnlyFactAttribute : FactAttribute
{
    public LinuxOnlyFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "Linux-only test (procfs/sysfs reads).";
    }
}
