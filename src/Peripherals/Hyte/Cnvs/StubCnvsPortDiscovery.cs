using System.Collections.Generic;

namespace Nexus.Service.Peripherals.Hyte.Cnvs;

/// <summary>No-op discovery for platforms with no real implementation.
/// Windows uses <see cref="WindowsCnvsPortDiscovery"/>, Linux uses
/// <see cref="LinuxCnvsPortDiscovery"/>.</summary>
public sealed class StubCnvsPortDiscovery : ICnvsPortDiscovery
{
    public IReadOnlyList<CnvsPortInfo> Discover() => System.Array.Empty<CnvsPortInfo>();
}
