using System.Collections.Generic;

namespace Nexus.Service.Monitoring.History;

/// <summary>One session change to persist. EndUtcSec is null for a session
/// still open (just started, or already open and unchanged).</summary>
public sealed record PrivacyAccessSessionUpdate(
    string Capability, string AppId, long StartUtcSec, long? EndUtcSec);

/// <summary>
/// Pure per-(capability, appId) session tracking: no I/O, fully
/// unit-testable. ConsentStore keeps only the latest session per app, so a
/// poll only ever sees one (start, stop) pair per key - comparing it against
/// what this instance last recorded is the only way to tell open, close, a
/// session that fully completed between two polls apart, and a session
/// superseded or dropped from the store while still open.
/// </summary>
public sealed class PrivacyAccessTransitions
{
    private sealed class TrackedSession
    {
        public long StartFileTime;
        public long StartUtcSec;
        public bool IsOpen;
    }

    private readonly Dictionary<(string Capability, string AppId), TrackedSession> _tracked = new();

    /// <summary>Advances tracking with one poll's raw entries and returns the
    /// session updates to persist (empty when nothing changed). nowUtcSec is
    /// this poll's time, used to close sessions this poll finds missing
    /// entirely (see the disappearance pass below).</summary>
    public IReadOnlyList<PrivacyAccessSessionUpdate> Advance(IReadOnlyList<PrivacyAccessRawEntry> snapshot, long nowUtcSec)
    {
        var updates = new List<PrivacyAccessSessionUpdate>();
        var seenKeys = new HashSet<(string Capability, string AppId)>();

        foreach (var entry in snapshot)
        {
            var startUtc = FileTimeConversion.ToUnixSeconds(entry.StartFileTime);
            if (startUtc is null)
            {
                continue;
            }

            var key = (entry.Capability, entry.AppId);
            seenKeys.Add(key);

            if (_tracked.TryGetValue(key, out var tracked) && tracked.StartFileTime == entry.StartFileTime)
            {
                if (tracked.IsOpen && entry.StopFileTime != 0)
                {
                    var stopUtc = FileTimeConversion.ToUnixSeconds(entry.StopFileTime);
                    if (stopUtc is not null)
                    {
                        updates.Add(new PrivacyAccessSessionUpdate(entry.Capability, entry.AppId, startUtc.Value, stopUtc));
                        tracked.IsOpen = false;
                    }
                    // else: an unconvertible stop value - leave the session
                    // tracked open so a later, valid stop can still close it.
                }
                continue;
            }

            // A Start we haven't recorded: the previous Start under this key,
            // if any, is superseded (ConsentStore overwrote it). If that
            // superseded session was still open, its real close was never
            // observed - close it now using the new session's start, since
            // that is the latest instant we know the old one was no longer
            // the active one.
            if (tracked is { IsOpen: true })
            {
                updates.Add(new PrivacyAccessSessionUpdate(
                    entry.Capability, entry.AppId, tracked.StartUtcSec, startUtc.Value));
            }

            // A non-zero StopFileTime that fails conversion (corrupt data) is
            // treated the same as "still open" rather than a fabricated
            // close, for the same reason as the branch above.
            var newStopUtc = entry.StopFileTime == 0 ? null : FileTimeConversion.ToUnixSeconds(entry.StopFileTime);
            updates.Add(new PrivacyAccessSessionUpdate(entry.Capability, entry.AppId, startUtc.Value, newStopUtc));
            _tracked[key] = new TrackedSession
            {
                StartFileTime = entry.StartFileTime,
                StartUtcSec = startUtc.Value,
                IsOpen = newStopUtc is null,
            };
        }

        // Keys tracked open from a previous poll but absent from this
        // snapshot (console user logoff/switch, or the ConsentStore entry
        // itself removed by an uninstall/privacy reset) never get a real
        // stop time from the registry - close them at this poll's time
        // rather than leaving them open forever.
        foreach (var (key, tracked) in _tracked)
        {
            if (tracked.IsOpen && !seenKeys.Contains(key))
            {
                updates.Add(new PrivacyAccessSessionUpdate(key.Capability, key.AppId, tracked.StartUtcSec, nowUtcSec));
                tracked.IsOpen = false;
            }
        }

        return updates;
    }
}
