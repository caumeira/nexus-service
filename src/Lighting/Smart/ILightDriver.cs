using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Lighting.Smart;

/// <summary>
/// Per-brand behavior for a network light family (Hue today; Nanoleaf, WLED,
/// LIFX, … next). One driver instance handles every device of its brand. The
/// brand-neutral plumbing (provider, registry, frame writer, throttle, routes,
/// settings) lives in the Smart namespace and calls into this contract — adding
/// a brand is implementing this interface + registering it in DI.
/// </summary>
public interface ILightDriver
{
    /// <summary>Id prefix for this brand, e.g. "hue". Device ids are
    /// "&lt;brand&gt;:&lt;...&gt;".</summary>
    string Brand { get; }

    /// <summary>Find controllers/lights of this brand on the LAN.</summary>
    Task<IReadOnlyList<DiscoveredLight>> DiscoverAsync(CancellationToken ct);

    /// <summary>Pair with a discovered target. May require a physical action
    /// (Hue bridge button); returns Ok=false + a human error if not ready.</summary>
    Task<PairResult> PairAsync(DiscoveredLight target, CancellationToken ct);

    /// <summary>Frame layout for a paired device (LED count + averaging mode).</summary>
    LightFramePlan PlanFrames(SmartLight dev);

    /// <summary>Apply the latest desired state (on/off + color + brightness).
    /// Called by the throttle, so it may run at up to <see cref="MinIntervalMs"/>.</summary>
    Task SendAsync(SmartLight dev, LightFrame frame, CancellationToken ct);

    /// <summary>Blink/breathe the device so the user can spot it physically.</summary>
    Task IdentifyAsync(SmartLight dev, CancellationToken ct);

    /// <summary>Minimum spacing between sends for this device (rate ceiling).</summary>
    int MinIntervalMs(SmartLight dev);
}
