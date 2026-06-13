using System.Collections.Generic;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting.Smart;

/// <summary>A light/controller found on the LAN during discovery, before pairing.</summary>
public sealed record DiscoveredLight(string Brand, string Host, string Name, string StableKey);

/// <summary>
/// How many engine <see cref="Engine.DeviceFrame"/> LEDs a device gets and
/// whether the writer averages them to one color before sending.
/// Single-color lamps (Hue bulbs, WiZ, Yeelight) request a small UV grid and
/// average it so screen-mirror / effects pick up the canvas region's dominant
/// color rather than a single point sample. Zone-addressable devices
/// (Nanoleaf panels, Govee segments) set AverageToSingle=false and may supply
/// per-LED canvas UVs (e.g. real panel positions) used instead of the default
/// sample grid. <paramref name="StaticNeedsStreaming"/> marks devices whose
/// realtime mode drops a manual color unless frames keep flowing (Govee
/// razer/DreamView): the provider then streams a solid zoned frame and the
/// writer keeps it alive instead of sending one colorwc.
/// </summary>
public sealed record LightFramePlan(int LedCount, bool AverageToSingle, float[]? LedU = null, float[]? LedV = null, bool StaticNeedsStreaming = false);

/// <summary>The latest desired state for one light. Coalesced by the throttle
/// and pushed to the device by its driver. Used for both effect streaming and
/// static (manual) control — one path. <see cref="Zones"/> carries the per-zone
/// RGB triplets (engine LED order) for zone-addressable devices while an effect
/// streams; null for single-color sends. R/G/B always hold the averaged color
/// so a driver can fall back to single-color regardless.</summary>
public readonly record struct LightFrame(bool On, byte R, byte G, byte B, float Brightness01, byte[]? Zones = null);

/// <summary>Runtime view of a paired smart light (the persisted
/// <see cref="SmartLightConfig"/> plus live online state).</summary>
public sealed class SmartLight
{
    public required string Id { get; init; }
    public required string Brand { get; init; }
    public required string Name { get; init; }
    public required string Host { get; init; }
    public required string StableKey { get; init; }
    public required string Token { get; init; }
    public required string Extra { get; init; }
    public bool Enabled { get; init; } = true;
    /// <summary>Last send/probe succeeded. Optimistically true until a failure;
    /// no background poll loop (a failed send flips it, a success flips it back).</summary>
    public bool Online { get; set; } = true;
}

/// <summary>Result of a pairing attempt. On success, <see cref="Devices"/> are
/// the entries to persist (one bridge press can yield many lights).</summary>
public sealed class PairResult
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    public List<SmartLightConfig> Devices { get; set; } = new();
}
