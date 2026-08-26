using System.Collections.Generic;

namespace Nexus.Service.Models.Conflicts;

/// <summary>
/// Single match returned by <see cref="Nexus.Service.Conflicts.ConflictWatcher"/>.
/// Pid is the lowest matched pid for the process name (the SPA only ever
/// asks to terminate by Id; the pid is informational).
/// </summary>
public sealed class DetectedConflict
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Category { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public int Pid { get; set; }
}

/// <summary>How a conflicting app is launched at login, when one was resolved.</summary>
public sealed class ConflictAutostartEntry
{
    /// <summary>"runKeyUser", "runKeyMachine" or "service".</summary>
    public string Kind { get; set; } = "";
    /// <summary>Run value name or service name; shown so the user sees what is removed.</summary>
    public string EntryName { get; set; } = "";
}

/// <summary>One detected conflict paired with its autostart entry, or null when none resolved.</summary>
public sealed class ConflictAutostartStatus
{
    public string Id { get; set; } = "";
    public ConflictAutostartEntry? Autostart { get; set; }
}

public sealed class GetConflictAutostartResponse
{
    public List<ConflictAutostartStatus> Apps { get; set; } = new();
}

/// <summary>Body for POST /conflicts/autostart/disable.</summary>
public sealed class DisableConflictAutostartBody
{
    public string Id { get; set; } = "";
}

public sealed class DisableConflictAutostartResponse
{
    public bool Ok { get; set; }
    public string Msg { get; set; } = "";
}

public sealed class GetConflictsResponse
{
    public List<DetectedConflict> Conflicts { get; set; } = new();
}

/// <summary>
/// Body for POST /conflicts/kill. Caller passes the catalog Id of the
/// conflict to terminate; the service resolves it back to the registered
/// process names and kills every matching pid.
/// </summary>
public sealed class KillConflictBody
{
    public string Id { get; set; } = "";
}

public sealed class KillConflictResponse
{
    public bool Error { get; set; }
    public string Msg { get; set; } = "Ok";
    /// <summary>True if at least one matching process was found and a kill attempted.</summary>
    public bool Killed { get; set; }
}

/// <summary>
/// WebSocket push frame on topic "conflicts". Sent each time the detected
/// set changes (add, remove, or pid shift). The list is the full current
/// snapshot - clients overwrite rather than diff.
/// </summary>
public sealed class ConflictsFrame
{
    public List<DetectedConflict> Conflicts { get; set; } = new();
}
