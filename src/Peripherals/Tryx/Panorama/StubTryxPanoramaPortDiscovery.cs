using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.Tryx.Panorama;

/// <summary>No-op discovery for platforms without Tryx Panorama support (macOS, etc.).</summary>
public sealed class StubTryxPanoramaPortDiscovery : ITryxPanoramaPanelDiscovery
{
    public IReadOnlyList<TryxPanoramaPortInfo> Discover() => Array.Empty<TryxPanoramaPortInfo>();
}
