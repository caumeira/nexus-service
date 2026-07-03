using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>No-op discovery for platforms without SLV3 dongle support (macOS, Linux).</summary>
public sealed class StubSlv3Discovery : ISlv3Discovery
{
    public IReadOnlyList<Slv3PortInfo> Discover() => Array.Empty<Slv3PortInfo>();
}
