using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.Tryx.Panorama;

public interface ITryxPanoramaPanelDiscovery
{
    IReadOnlyList<TryxPanoramaPortInfo> Discover();
}

public sealed class TryxPanoramaPortInfo
{
    public required string PortName { get; init; }
    public string Serial { get; init; } = "";
    public int ProductId { get; init; }
    public string AdbSerial { get; init; } = "";
}

public interface ITryxPanoramaTransport : IDisposable
{
    bool IsOpen { get; }
    string Serial { get; }
    string PortName { get; }
    void Write(ReadOnlySpan<byte> data);
}
