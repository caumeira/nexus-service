using System.Collections.Generic;

namespace Nexus.Service.Peripherals.Hyte.Cnvs;

/// <summary>No-op discovery for non-Windows targets. CNVS firmware
/// settings are only meaningful on Windows hosts today.</summary>
public sealed class StubCnvsPortDiscovery : ICnvsPortDiscovery
{
    public IReadOnlyList<CnvsPortInfo> Discover() => System.Array.Empty<CnvsPortInfo>();
}
