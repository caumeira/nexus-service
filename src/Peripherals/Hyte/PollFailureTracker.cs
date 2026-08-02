using System.Threading;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Hyte;

/// <summary>
/// Consecutive-failure counter for hub polls sharing a transport with the
/// 30 Hz lighting writer: dropping the port blanks that hub's LEDs, and a
/// desynced reply resyncs on the next poll's DiscardInput anyway.
/// </summary>
public sealed class PollFailureTracker
{
    public const int Threshold = 3;

    private readonly string _tag;
    private int _consecutive;

    public PollFailureTracker(string tag) => _tag = tag;

    public int Consecutive => Volatile.Read(ref _consecutive);

    public void Reset() => Interlocked.Exchange(ref _consecutive, 0);

    /// <summary>Record a failed poll; true when the caller should drop the transport.</summary>
    public bool ShouldDisconnect(string operation, string detail)
    {
        var n = Interlocked.Increment(ref _consecutive);
        ServiceLog.Warn($"[{_tag}] {operation} poll failed (#{n}/{Threshold}): {detail}");
        if (n < Threshold)
        {
            return false;
        }

        ServiceLog.Warn($"[{_tag}] {n} consecutive poll failures - dropping transport so next tick rediscovers");
        Interlocked.Exchange(ref _consecutive, 0);
        return true;
    }
}
