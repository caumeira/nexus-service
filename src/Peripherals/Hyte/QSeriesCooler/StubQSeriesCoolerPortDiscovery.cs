using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.Hyte.QSeriesCooler;

/// <summary>No-op discovery for platforms with no real implementation
/// (macOS).</summary>
public sealed class StubQSeriesCoolerPortDiscovery : IQSeriesCoolerPortDiscovery
{
    public IReadOnlyList<QSeriesCoolerPort> Discover() => Array.Empty<QSeriesCoolerPort>();
}
