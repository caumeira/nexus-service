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
}
