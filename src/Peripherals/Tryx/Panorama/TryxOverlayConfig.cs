namespace Nexus.Service.Peripherals.Tryx.Panorama;

/// <summary>
/// Overlay configuration the Tryx Panorama renders on top of the playing media.
/// Stored on the hub and threaded into every config frame so changes take effect
/// on the next config send without a media reload.
/// Stats: up to 3 sysinfoDisplay label strings (e.g. "CPU Temperature").
/// </summary>
public sealed class TryxOverlayConfig
{
    public string[] Stats { get; set; } = [];
    public string Color { get; set; } = "#000000";
    public string Align { get; set; } = "Left";
    public string? Filter { get; set; }
    public int Opacity { get; set; } = 100;

    /// <summary>Per-stat normalized (0..1) top-left position of the value text,
    /// parallel to <see cref="Stats"/>; a stat past the end of these arrays falls
    /// back to <see cref="TryxRkProtocol"/>'s default stack.</summary>
    public double[] PosX { get; set; } = [];
    public double[] PosY { get; set; } = [];
    public string Font { get; set; } = "roboto-regular";
    public int Size { get; set; } = 100;
}
