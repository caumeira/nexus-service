using System.Collections.Generic;

namespace Nexus.Service.Peripherals.Hyte.QSeriesCooler;

/// <summary>
/// Discovers attached Q-series cooler controllers at the OS layer. Windows
/// uses SetupAPI to find COM ports with hardware id
/// <c>USB\VID_3402&amp;PID_0400</c> (Q60) or <c>…PID_0403</c> (Q80); Linux walks
/// sysfs; macOS returns empty.
/// </summary>
public interface IQSeriesCoolerPortDiscovery
{
    IReadOnlyList<QSeriesCoolerPort> Discover();
}

/// <summary>One Q-series cooler controller as seen by the OS, before we open it.</summary>
public sealed class QSeriesCoolerPort
{
    /// <summary>Serial COM port name, e.g. "COM7" on Windows.</summary>
    public required string PortName { get; init; }

    /// <summary>USB instance-id segment, or empty when unavailable.</summary>
    public string Serial { get; init; } = "";

    /// <summary>"q60" or "q80", derived from the matched USB PID.</summary>
    public required string Variant { get; init; }
}
