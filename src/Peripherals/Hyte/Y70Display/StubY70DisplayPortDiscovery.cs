using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.Hyte.Y70Display;

/// <summary>No-op discovery for platforms with no real implementation
/// (macOS).</summary>
public sealed class StubY70DisplayPortDiscovery : IY70DisplayPortDiscovery
{
    public IReadOnlyList<Y70DisplayPort> Discover() => Array.Empty<Y70DisplayPort>();
}
