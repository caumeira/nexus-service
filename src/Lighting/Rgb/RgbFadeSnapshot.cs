using System.Collections.Generic;

namespace Nexus.Service.Lighting.Rgb;

/// <summary>
/// What <see cref="RgbBridge.CaptureFadeSnapshot"/> hands back: the colours each
/// OpenRGB physical device was showing when a fade engaged, a reusable scratch
/// buffer per device to scale into, and which devices the fade must skip.
/// Opaque to callers - only the bridge reads it.
/// </summary>
public sealed class RgbFadeSnapshot
{
    internal Dictionary<int, RgbColor[]> Baseline { get; } = new();
    internal Dictionary<int, RgbColor[]> Scratch { get; } = new();
    internal Dictionary<int, bool> Uncontrolled { get; } = new();
}
