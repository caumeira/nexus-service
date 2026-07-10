using Nexus.Service.Models.Panel;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Render + encode parameters for one streamed panel device kind. The
/// profile is the headless-config source of truth: it stamps the panel
/// record's capabilities and sizes the overlay's off-screen render host.
/// Per-serial overrides (fps/bitrate/profile kind) live in
/// <see cref="StreamedPanelStore"/>.
/// </summary>
public sealed class StreamedPanelProfile
{
    /// <summary>Stable id persisted as a per-serial override key.</summary>
    public required string Kind { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>PanelSurfaces value stamped on the record; picks the default layout.</summary>
    public required string Surface { get; init; }

    public required int CssWidth { get; init; }
    public required int CssHeight { get; init; }
    public double Dpr { get; init; } = 1.0;
    public int Fps { get; init; } = 60;
    public int BitrateKbps { get; init; } = 8000;

    /// <summary>
    /// Frames concatenated into one transport write. The paced writer ticks
    /// at Fps/batch and sends the batch as a single write, so a transport
    /// whose throughput is bounded per round trip (one ack per write, as on
    /// the D213's adb chain) carries batch-times more frames per second. A
    /// display-rate-bound device gains nothing: excess frames congest the
    /// chain and the queue trims. Above 1 the device shows frames in bursts
    /// of this size, so keep it at 1 unless the transport is the ceiling.
    /// </summary>
    public int WriteBatchFrames { get; init; } = 1;

    public PanelDeviceCapabilities BuildCapabilities() => new()
    {
        Surface = Surface,
        Touch = false,
        CssWidth = CssWidth,
        CssHeight = CssHeight,
        Dpr = Dpr,
    };
}
