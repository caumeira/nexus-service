namespace Nexus.Service.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that is skipped on Windows. For tests bound to
/// Unix filesystem semantics (a fake <c>/sys</c> tree, symlinks) or that collide
/// with a running Windows service holding a shared file. They still run (and are
/// asserted) on macOS/Linux, the platforms where the behaviour applies.
/// </summary>
public sealed class NonWindowsFactAttribute : FactAttribute
{
    public NonWindowsFactAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "Unix-only test (filesystem semantics or running-service file collision).";
    }
}
