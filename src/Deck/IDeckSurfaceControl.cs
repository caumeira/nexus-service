namespace Nexus.Service.Deck;

/// <summary>
/// Narrow surface-control seam StreamDeckConnectionWorker exposes to
/// DeckActionExecutor for deckBrightness/deckSleep dispatch. Constructor
/// injection cannot wire this directly (the worker also depends on
/// IDeckActionExecutor to dispatch key presses, which would be a circular
/// dependency) - callers hold this behind a Lazy so resolution is deferred
/// until dispatch time, by which point the worker singleton already exists.
/// See the Lazy&lt;IDeckSurfaceControl&gt; registration in
/// NexusServiceCollectionExtensions.
/// </summary>
public interface IDeckSurfaceControl
{
    /// <summary>Sets a connected deck's display brightness live. No-op if the serial has no live surface.</summary>
    void SetBrightness(string serial, int percent);

    /// <summary>
    /// Blanks a connected deck's display and marks it asleep, reusing the
    /// sleep-after-idle wake path - the next key press restores the
    /// persisted brightness. No-op if the serial has no live surface.
    /// </summary>
    void PutAsleep(string serial);
}
