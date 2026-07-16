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
/// what this instance last recorded is the only way to tell open, close, and
/// a session that fully completed between two polls apart.
/// </summary>
public sealed class PrivacyAccessTransitions
{
    private sealed class TrackedSession
    {
        public long StartFileTime;
        public bool IsOpen;
    }

    private readonly Dictionary<(string Capability, string AppId), TrackedSession> _tracked = new();

    /// <summary>Advances tracking with one poll's raw entries and returns the
    /// session updates to persist (empty when nothing changed).</summary>
    public IReadOnlyList<PrivacyAccessSessionUpdate> Advance(IReadOnlyList<PrivacyAccessRawEntry> snapshot)
    {
        var updates = new List<PrivacyAccessSessionUpdate>();

        foreach (var entry in snapshot)
        {
            var startUtc = FileTimeConversion.ToUnixSeconds(entry.StartFileTime);
            if (startUtc is null)
            {
                continue;
            }

            var key = (entry.Capability, entry.AppId);
            if (_tracked.TryGetValue(key, out var tracked) && tracked.StartFileTime == entry.StartFileTime)
            {
                if (tracked.IsOpen && entry.StopFileTime != 0)
                {
                    updates.Add(new PrivacyAccessSessionUpdate(
                        entry.Capability, entry.AppId, startUtc.Value, FileTimeConversion.ToUnixSeconds(entry.StopFileTime)));
                    tracked.IsOpen = false;
                }
                continue;
            }

            // A Start we haven't recorded: whatever this key's previous
            // Start was is superseded (ConsentStore overwrote it), so its
            // real stop time, if we never saw it, cannot be recovered - it
            // is dropped rather than fabricated.
            var isOpen = entry.StopFileTime == 0;
            updates.Add(new PrivacyAccessSessionUpdate(
                entry.Capability, entry.AppId, startUtc.Value,
                isOpen ? null : FileTimeConversion.ToUnixSeconds(entry.StopFileTime)));
            _tracked[key] = new TrackedSession { StartFileTime = entry.StartFileTime, IsOpen = isOpen };
        }

        return updates;
    }
}
