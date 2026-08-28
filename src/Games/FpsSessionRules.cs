namespace Nexus.Service.Games;

/// <summary>
/// Session-validity constants from the fps-benchmarks plan's decision 4/6,
/// kept in one place so the recorder, the store's prune logic, and tests
/// never drift against each other.
/// </summary>
public static class FpsSessionRules
{
    /// <summary>A second's frame count counts toward validSec/frames/hist
    /// only within this inclusive range; 0 (loading, paused, minimized) and
    /// anything above are dropped, not clamped.</summary>
    public const int MinValidFps = 1;
    public const int MaxValidFps = 1000;

    /// <summary>A session persists only once its wall-clock focused time
    /// reaches this many seconds; shorter sessions are discarded entirely.</summary>
    public const int MinFocusedSecToPersist = 300;

    /// <summary>A session is flagged capped when p90 - p10 (in fps) is at or
    /// below this spread - v-sync/frame-limiter flat lining, not a real
    /// measurement ceiling.</summary>
    public const int CappedSpreadFps = 2;

    public static bool IsValidFrameCount(int frames) => frames is >= MinValidFps and <= MaxValidFps;
}
