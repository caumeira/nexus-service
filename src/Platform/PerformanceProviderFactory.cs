namespace Nexus.Service.Platform;

/// <summary>
/// Non-Windows IPerformanceProvider factory. Windows is registered via DI
/// directly in Program.cs because its impl needs the shared LhmComputer;
/// macOS and Linux are dependency-free so a plain static factory is fine.
/// The target OS is fixed per published RID, so the impl is a compile-time
/// pick — the other platform's provider file isn't compiled into the binary.
/// </summary>
public static class PerformanceProviderFactory
{
    public static IPerformanceProvider Create() =>
#if MACOS
        new Mac.MacPerformanceProvider();
#elif LINUX
        new Linux.LinuxPerformanceProvider();
#else
        throw new System.PlatformNotSupportedException(
            "PerformanceProviderFactory is macOS/Linux only; Windows uses DI.");
#endif
}
