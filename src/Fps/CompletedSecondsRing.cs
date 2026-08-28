namespace Nexus.Service.Fps;

/// <summary>
/// Per-second frame ring backing IFpsProvider.ReadCompletedSeconds: a slot
/// accumulates as frames for that second are recorded; a second becomes
/// readable once wall-clock time has moved past it by completionLagSec,
/// never by waiting for a later frame to arrive - so a paused or minimized
/// target (no frames at all for a stretch) still reports its 0-frame
/// seconds instead of leaving them stuck open. No Windows/ETW dependency,
/// so the wall-clock completion and ring-wraparound logic is directly
/// unit-testable.
/// </summary>
internal sealed class CompletedSecondsRing
{
    private const long SecUnset = long.MinValue;

    private readonly long[] _ts;
    private readonly int[] _frames;
    private readonly int _completionLagSec;
    private long _trackingStartSec = SecUnset;

    public CompletedSecondsRing(int ringSize, int completionLagSec)
    {
        _ts = new long[ringSize];
        _frames = new int[ringSize];
        _completionLagSec = completionLagSec;
        Reset();
    }

    public bool IsTracking => _trackingStartSec != SecUnset;

    /// <summary>Clears every slot and stops tracking (ReadCompleted returns
    /// nothing until StartTracking is called again).</summary>
    public void Reset()
    {
        Array.Fill(_ts, SecUnset);
        Array.Clear(_frames);
        _trackingStartSec = SecUnset;
    }

    /// <summary>Begins tracking at nowSec: no second before this ts is ever
    /// reported, even if the ring still physically holds stale data from a
    /// previous target.</summary>
    public void StartTracking(long nowSec) => _trackingStartSec = nowSec;

    public void RecordFrame(long tsSec)
    {
        var idx = Index(tsSec);
        if (_ts[idx] != tsSec)
        {
            _ts[idx] = tsSec;
            _frames[idx] = 0;
        }
        _frames[idx]++;
    }

    /// <summary>Every completed second with ts &gt; afterTsSec, oldest
    /// first, capped to one ring's worth per call. frames is 0 for a second
    /// nothing was recorded for (no presents that second, or it fell out of
    /// the ring from the caller being more than a ring's width behind).</summary>
    public IReadOnlyList<(long TsSec, int Frames)> ReadCompleted(long afterTsSec, long nowSec)
    {
        var result = new List<(long, int)>();
        if (!IsTracking)
        {
            return result;
        }

        var completionThreshold = nowSec - _completionLagSec;
        var start = Math.Max(afterTsSec + 1, _trackingStartSec);
        var end = Math.Min(completionThreshold, start + _ts.Length - 1);
        for (var ts = start; ts <= end; ts++)
        {
            var idx = Index(ts);
            var frames = _ts[idx] == ts ? _frames[idx] : 0;
            result.Add((ts, frames));
        }
        return result;
    }

    private int Index(long tsSec) => (int)(((tsSec % _ts.Length) + _ts.Length) % _ts.Length);
}
