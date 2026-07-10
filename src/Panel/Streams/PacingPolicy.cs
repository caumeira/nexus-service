using System;

namespace Nexus.Service.Panel.Streams;

/// <summary>Result of one pacing tick: drop DropCount frames from the queue
/// head, then send SendCount frames; ClearWaitingForIdr resets the writer's
/// resync latch.</summary>
public readonly record struct PacingDecision(int DropCount, int SendCount, bool ClearWaitingForIdr);

/// <summary>
/// Per-tick send/drop decision for the paced device writer. A resyncing
/// writer (waitingForIdr) may only drop a contiguous run ending at the next
/// IDR frame, never skip frames mid-GOP once streaming resumes.
/// </summary>
public static class PacingPolicy
{
    public const int CatchUpDepth = 6;

    public static PacingDecision Decide(int queueDepth, int framesUntilIdr, bool waitingForIdr)
    {
        if (waitingForIdr)
        {
            if (framesUntilIdr < 0)
            {
                return new PacingDecision(0, 0, false);
            }

            var remaining = queueDepth - framesUntilIdr;
            var send = Math.Min(remaining > CatchUpDepth ? 2 : 1, remaining);
            return new PacingDecision(framesUntilIdr, send, true);
        }

        if (queueDepth == 0)
        {
            return new PacingDecision(0, 0, false);
        }

        var sendCount = Math.Min(queueDepth > CatchUpDepth ? 2 : 1, queueDepth);
        return new PacingDecision(0, sendCount, false);
    }
}
