using System.Collections.Generic;

namespace Nexus.Service.Peripherals.Tryx.Panorama;

/// <summary>One overlay item: a live sensor reading (resolved by <see cref="Device"/>
/// + <see cref="SensorId"/> from <c>ISensorProvider</c>) rendered as a value/label
/// pair at the normalized (0..1) justification anchor (<see cref="X"/>, <see cref="Y"/>;
/// Y is the top of the value text).</summary>
public sealed class TryxOverlaySensorItem
{
    public string SensorId { get; set; } = "";
    public string Device { get; set; } = "";
    public string Label { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
}

/// <summary>
/// Overlay configuration the Tryx Panorama renders on top of the playing media.
/// Stored on the hub and threaded into every config frame so changes take effect
/// on the next config send without a media reload. Up to 4 <see cref="Items"/>.
/// </summary>
public sealed class TryxOverlayConfig
{
    public List<TryxOverlaySensorItem> Items { get; set; } = new();
    public string Color { get; set; } = "#000000";
    /// <summary>"left", "center", or "right".</summary>
    public string Align { get; set; } = "left";
    public string? Filter { get; set; }
    public int Opacity { get; set; } = 100;
    public string Font { get; set; } = "roboto-regular";
    public int Size { get; set; } = 100;
    /// <summary>Web-only UX state (positions derived from align); the service
    /// always renders from each item's own X/Y and never reads this.</summary>
    public bool Docked { get; set; }
}
