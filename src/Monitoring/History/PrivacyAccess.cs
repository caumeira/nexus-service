using System.Collections.Generic;

namespace Nexus.Service.Monitoring.History;

/// <summary>
/// Shared constants for privacy access session tracking (which apps used the
/// microphone/webcam/location/screen-capture capabilities, as timed
/// sessions), so the watcher, store, and route agree on retention and which
/// CapabilityAccessManager\ConsentStore subkeys to read.
/// </summary>
public static class PrivacyAccess
{
    public const int RetentionDays = 30;

    /// <summary>ConsentStore capability subkey names this feature tracks;
    /// ConsentStore carries ~20 more (contacts, calendar, etc.) left unread.</summary>
    public static readonly IReadOnlyList<string> TrackedCapabilities = new[]
    {
        "microphone",
        "location",
        "webcam",
        "graphicsCaptureProgrammatic",
        "graphicsCaptureWithoutBorder",
    };
}
