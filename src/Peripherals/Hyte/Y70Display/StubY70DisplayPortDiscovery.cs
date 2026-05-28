using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.Hyte.Y70Display;

/// <summary>No-op discovery for non-Windows builds — the SetupAPI COM-port walk is Windows-only.</summary>
public sealed class StubY70DisplayPortDiscovery : IY70DisplayPortDiscovery
{
    public IReadOnlyList<Y70DisplayPort> Discover() => Array.Empty<Y70DisplayPort>();
}
