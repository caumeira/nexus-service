using System;
using System.Collections.Generic;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Latest touch-routing state the macOS overlay helper reported for the one
/// promoted panel it routes a touchscreen to. "permission-needed" means its
/// event tap was refused (no Privacy grant), so touches land on the main
/// display; the helper retries on its own, and reports "active" once the
/// grant lands.
/// </summary>
public sealed class TouchRoutingStatus
{
    public const string Active = "active";
    public const string PermissionNeeded = "permission-needed";
    public const string Idle = "idle";

    /// <summary>Panel record warning code for a display whose touch routing lacks the Privacy grant.</summary>
    public const string PermissionWarning = "touch-permission";

    private readonly object _gate = new();
    private string _displayId = "";
    private string _state = Idle;

    public static bool IsValidState(string? state) => state is Active or PermissionNeeded or Idle;

    /// <summary>
    /// Records a report. Returns the display ids whose warning changed, so
    /// the caller can re-broadcast exactly those panel records.
    /// </summary>
    public IReadOnlyList<string> Report(string displayId, string state)
    {
        lock (_gate)
        {
            var before = WarningDisplayLocked();
            _displayId = displayId;
            _state = state;
            var after = WarningDisplayLocked();
            if (before == after) return Array.Empty<string>();
            var changed = new List<string>(2);
            if (before.Length > 0) changed.Add(before);
            if (after.Length > 0) changed.Add(after);
            return changed;
        }
    }

    /// <summary>The warning code for a display-bound panel record, or null.</summary>
    public string? WarningFor(string? displayId)
    {
        if (string.IsNullOrEmpty(displayId)) return null;
        lock (_gate)
        {
            return string.Equals(WarningDisplayLocked(), displayId, StringComparison.Ordinal) ? PermissionWarning : null;
        }
    }

    // Caller holds _gate. "" when no display carries the warning.
    private string WarningDisplayLocked() => _state == PermissionNeeded ? _displayId : "";
}
