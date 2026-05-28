using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.Hyte.QSeriesCooler;

/// <summary>No-op discovery for non-Windows builds — the SetupAPI COM-port walk is Windows-only.</summary>
public sealed class StubQSeriesCoolerPortDiscovery : IQSeriesCoolerPortDiscovery
{
    public IReadOnlyList<QSeriesCoolerPort> Discover() => Array.Empty<QSeriesCoolerPort>();
}
