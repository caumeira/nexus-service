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

    /// <summary>Wallpaper preset ids (e.g. "default_01") the panel reported it has
    /// stored on device. Empty when the transport has not received or does not
    /// support the media-list push (see <see cref="TryxMediaList"/>).</summary>
    IReadOnlyList<string> AvailableMediaIds => Array.Empty<string>();

    /// <summary>All media filenames the panel last reported it has stored (preset,
    /// download, and custom), or empty if it has not pushed its list this session.</summary>
    IReadOnlyList<string> AvailableMediaFilenames => Array.Empty<string>();
}
