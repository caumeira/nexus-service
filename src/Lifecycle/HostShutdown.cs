namespace Nexus.Service.Lifecycle;

/// <summary>
/// Whether the process is stopping because the OS itself is going down
/// (SCM SERVICE_CONTROL_SHUTDOWN) rather than a plain service stop or
/// restart. Device transports consult this to run last-chance device
/// hygiene that only makes sense when the host is about to reboot.
/// </summary>
public static class HostShutdown
{
    public static volatile bool IsOsShutdown;
}
