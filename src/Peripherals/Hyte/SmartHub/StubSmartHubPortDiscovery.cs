using System.Collections.Generic;
using Nexus.Service.Peripherals.Hyte.Np50;

namespace Nexus.Service.Peripherals.Hyte.SmartHub;

public sealed class StubSmartHubPortDiscovery : INp50PortDiscovery
{
    public IReadOnlyList<Np50PortInfo> Discover() => System.Array.Empty<Np50PortInfo>();
}
