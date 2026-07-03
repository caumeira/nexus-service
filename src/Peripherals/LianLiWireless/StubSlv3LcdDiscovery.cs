using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>No-op discovery for platforms without SL-LCD Wireless screen support (macOS, Linux).</summary>
public sealed class StubSlv3LcdDiscovery : ISlv3LcdDiscovery
{
    public IReadOnlyList<Slv3LcdPortInfo> Discover() => Array.Empty<Slv3LcdPortInfo>();
}
