using System.Collections.Generic;

namespace Nexus.Service.Peripherals.Hyte.Np50;

/// <summary>
/// Discovery stub for platforms with no real implementation (macOS dev
/// builds). Returns an empty list. Windows uses
/// <see cref="WindowsNp50PortDiscovery"/> and Linux uses
/// <see cref="LinuxNp50PortDiscovery"/>.
/// </summary>
public sealed class StubNp50PortDiscovery : INp50PortDiscovery
{
    public IReadOnlyList<Np50PortInfo> Discover() => System.Array.Empty<Np50PortInfo>();
}
