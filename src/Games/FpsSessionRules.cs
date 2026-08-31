namespace Nexus.Service.Games;

/// <summary>Session-validity constants kept in one place so the recorder,
/// the store's prune logic, and tests never drift against each other.</summary>
public static class FpsSessionRules
{
    /// <summary>A second's frame count counts toward validSec/frames/hist
    /// only within this inclusive range; 0 (loading, paused, minimized) and
    /// anything above are dropped, not clamped.</summary>
    public const int MinValidFps = 1;
    public const int MaxValidFps = 1000;

    /// <summary>A session is uploaded only once its wall-clock focused time
    /// reaches this many seconds. Shorter sessions are still kept on disk and
    /// shown in the local history - the bar is a sampling-quality rule for the
    /// shared leaderboard, not a reason to throw a player's own data away.</summary>
    public const int MinFocusedSecToUpload = 300;

    /// <summary>A session is flagged capped when p90 - p10 (in fps) is at or
    /// below this spread - v-sync/frame-limiter flat lining, not a real
    /// measurement ceiling.</summary>
    public const int CappedSpreadFps = 2;

    public static bool IsValidFrameCount(int frames) => frames is >= MinValidFps and <= MaxValidFps;
}
