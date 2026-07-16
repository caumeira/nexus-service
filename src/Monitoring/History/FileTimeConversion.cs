namespace Nexus.Service.Monitoring.History;

/// <summary>
/// Converts a Windows FILETIME (100ns intervals since 1601-01-01 UTC, the
/// unit CapabilityAccessManager\ConsentStore stores LastUsedTimeStart/Stop
/// in) to Unix epoch seconds, the time base every other table in metrics.db
/// uses. Pure and platform-independent - no Windows API involved.
/// </summary>
public static class FileTimeConversion
{
    private const long TicksPerSecond = 10_000_000L;

    // Ticks between the FILETIME epoch (1601-01-01) and the Unix epoch
    // (1970-01-01).
    private const long FileTimeToUnixEpochTicks = 116_444_736_000_000_000L;

    /// <summary>Null for a non-positive fileTime - ConsentStore uses 0 for
    /// LastUsedTimeStop to mean "still in use", not epoch zero.</summary>
    public static long? ToUnixSeconds(long fileTime) =>
        fileTime <= 0 ? null : (fileTime - FileTimeToUnixEpochTicks) / TicksPerSecond;
}
