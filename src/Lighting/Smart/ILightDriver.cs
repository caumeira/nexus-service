using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Lighting.Smart;

/// <summary>
/// Per-brand behavior for a network light family (Hue today; Nanoleaf, WLED,
/// LIFX, … next). One driver instance handles every device of its brand. The
/// brand-neutral plumbing (provider, registry, frame writer, throttle, routes,
/// settings) lives in the Smart namespace and calls into this contract - adding
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

    /// <summary>Key identifying the shared controller this device's sends are
    /// rate-limited against. Devices returning the same key share one rate
    /// budget - e.g. all bulbs on a Hue bridge return the bridge host so they
    /// can't collectively flood it. Per-device controllers return their own id.</summary>
    string RateLimitKey(SmartLight dev);

    /// <summary>Cheap reachability probe - is the controller answering right now?
    /// Drives accurate online status, instead of inferring it from send failures
    /// (which flip false under streaming load and never recover).</summary>
    Task<bool> PingAsync(SmartLight dev, CancellationToken ct);
}

/// <summary>
/// Optional capability for drivers that stream effect frames over a dedicated
/// low-latency session (all of a controller's lights in one batch/packet) rather
/// than per-light commands - e.g. Hue Entertainment (DTLS UDP). The frame writer
/// accumulates a color per device each tick, flushes once, and ends the session
/// when the effect stops.
/// </summary>
public interface ISessionStreamer
{
    /// <summary>Can this device stream right now (a session is active, starting,
    /// or has never failed)? False routes the caller to <see cref="ILightDriver.SendAsync"/>
    /// instead of <see cref="Accumulate"/> - e.g. Hue with no Entertainment Area.</summary>
    bool CanStream(SmartLight dev);

    /// <summary>Buffer this device's color for the current frame (already
    /// brightness-applied; off = black).</summary>
    void Accumulate(SmartLight dev, byte r, byte g, byte b);

    /// <summary>Send the buffered frame to every active session; lazily open
    /// sessions (async, in the background) for controllers seen this tick.</summary>
    void Flush();

    /// <summary>Tear down all sessions (effect stopped / shutdown).</summary>
    void StopAll();
}
