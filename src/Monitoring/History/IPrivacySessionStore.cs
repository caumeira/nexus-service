using System.Collections.Generic;

namespace Nexus.Service.Monitoring.History;

/// <summary>One recorded privacy-capability access session. EndUtcSec null
/// means the app is using the capability right now.</summary>
public sealed record PrivacySession(string AppId, string Capability, long StartUtcSec, long? EndUtcSec);

/// <summary>
/// Persistent store for privacy access sessions. PrivacyAccessWatcher is the
/// only writer, one Upsert per transition (open, close, or a session missed
/// between two polls); GET /monitoring/privacy is the reader.
/// </summary>
public interface IPrivacySessionStore
{
    /// <summary>Inserts or updates the session keyed by (appId, capability,
    /// startUtcSec); endUtcSec null means the session is still open.</summary>
    void Upsert(string capability, string appId, long startUtcSec, long? endUtcSec);

    /// <summary>Sessions overlapping [fromSec, toSec], ascending by start.
    /// An open session (EndUtcSec null) overlaps whenever its start is at or
    /// before toSec, since it has no upper bound yet.</summary>
    IReadOnlyList<PrivacySession> Query(long fromSec, long toSec);

    /// <summary>Deletes closed sessions that ended before cutoffSec. An open
    /// session is never pruned regardless of how old its start is.</summary>
    void PruneOlderThan(long cutoffSec);
}
