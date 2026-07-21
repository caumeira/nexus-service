using System.Collections.Generic;

namespace Nexus.Service.Monitoring.Events;

/// <summary>
/// Persistent store for monitoring timeline events. MonitoringEventCollector
/// (usb attach/detach, app-open/uac-escalation) is the only automatic
/// producer; POST /monitoring/events is the only source of custom events;
/// GET /monitoring/events is the reader. Privacy-capability access is
/// deliberately NOT written here - it stays in IPrivacySessionStore/
/// GET /monitoring/privacy, merged into the timeline web-side.
/// </summary>
public interface IMonitoringEventStore
{
    /// <summary>Appends a new event and returns it with its assigned id.</summary>
    MonitoringEvent Append(long tUtcMs, string kind, string label, string? detail, bool custom);

    /// <summary>Events with TUtcMs in [fromUtcMs, toUtcMs], ascending by
    /// TUtcMs. When more events match than limit, keeps the newest limit of
    /// them (still returned ascending).</summary>
    IReadOnlyList<MonitoringEvent> Query(long fromUtcMs, long toUtcMs, int limit);

    /// <summary>Deletes the event with this id if it exists and is Custom.
    /// Returns false when no such id exists or the event is not custom.</summary>
    bool DeleteCustom(long id);

    /// <summary>Deletes every event older than cutoffUtcMs.</summary>
    void PruneOlderThan(long cutoffUtcMs);
}
