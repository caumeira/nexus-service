using System;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// Whether the process is stopping because the OS itself is going down
/// (SCM SERVICE_CONTROL_PRESHUTDOWN, or SERVICE_CONTROL_SHUTDOWN where
/// preshutdown is unavailable) rather than a plain service stop or restart.
/// Device transports consult this to run last-chance device hygiene that only
/// makes sense when the host is about to reboot.
///
/// Only ever set from WindowsServiceHost.ControlHandler. It stays false on a
/// shutdown unless the service accepts PRESHUTDOWN - see the constant there for
/// the measured five-second race that makes SERVICE_CONTROL_SHUTDOWN too late.
/// </summary>
public static class HostShutdown
{
    public static volatile bool IsOsShutdown;

    /// <summary>Cap on Program.FastServiceShutdown's parallel teardown set for a
    /// stop that is not the OS going down (tray quit, /service/stop, an OTA
    /// install, a GPU-change restart). Each of those is followed by a service
    /// that comes back, so the stop stays snappy.</summary>
    public static readonly TimeSpan FastTeardownBudget = TimeSpan.FromMilliseconds(1_500);

    /// <summary>Cap on the same set when the OS is going down. Higher because the
    /// OS-shutdown-only work is the slowest in it: the Q-series keyevent execs a
    /// JVM on the panel, and the rest of the set was measured at 1209 ms on a
    /// Y70. The window this spends from is the preshutdown phase - see the
    /// SERVICE_ACCEPT_PRESHUTDOWN constant in WindowsServiceHost.</summary>
    public static readonly TimeSpan OsShutdownTeardownBudget = TimeSpan.FromMilliseconds(3_000);
}
