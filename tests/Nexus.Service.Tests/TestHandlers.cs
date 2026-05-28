using System;
using Nexus.Service.Devices.Handlers;
using Nexus.Service.Peripherals.Hyte.Cnvs;
using Nexus.Service.Peripherals.Hyte.MiniHub;
using Nexus.Service.Peripherals.Hyte.QSeriesCooler;

namespace Nexus.Service.Tests;

/// <summary>
/// Builds device handlers backed by stub port-discovery hubs for detection
/// tests. The stub discovery never opens a port, so the hubs report no
/// connection and an empty firmware version — exactly the state the detection
/// tests assume.
/// </summary>
internal static class TestHandlers
{
    public static CnvsHandler Cnvs() => new(new CnvsHub(new StubCnvsPortDiscovery()));

    public static FanHubHandler FanHub() => new(new MiniHubHub(
        new StubMiniHubPortDiscovery(),
        _ => throw new InvalidOperationException("MiniHub transport is not expected in detection tests")));

    public static QSeriesHandler QSeries() => new(new QSeriesCoolerHub(
        new StubQSeriesCoolerPortDiscovery(),
        _ => throw new InvalidOperationException("Q-series transport is not expected in detection tests")));
}
