using System.Runtime.InteropServices;
using Nexus.Service.Platform.Linux;
using Nexus.Service.Platform.Mac;

namespace Nexus.Service.Platform;

/// <summary>
/// Non-Windows IPerformanceProvider factory. Windows is registered via DI
/// directly in Program.cs because its impl needs the shared LhmComputer;
/// macOS and Linux are dependency-free so a plain static factory is fine.
/// </summary>
public static class PerformanceProviderFactory
{
    public static IPerformanceProvider Create()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new MacPerformanceProvider();
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return new LinuxPerformanceProvider();
        }
        // Fallback: return the Linux impl (harmless no-op on non-Linux since
        // the /proc reads just fail silently and return null). Never hit in
        // practice -- Windows path takes the DI branch in Program.cs.
        return new LinuxPerformanceProvider();
    }
}
