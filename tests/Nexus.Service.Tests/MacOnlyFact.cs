namespace Nexus.Service.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that only runs on macOS, for tests exercising
/// mac syscalls (proc_pidinfo, sysctl). Reported as skipped elsewhere so the
/// suite's green never claims a verification that did not happen.
/// </summary>
public sealed class MacOnlyFactAttribute : FactAttribute
{
    public MacOnlyFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
            Skip = "macOS-only test (mac kernel syscalls).";
    }
}
