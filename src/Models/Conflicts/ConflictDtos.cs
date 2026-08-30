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
    /// <summary>True when the app was running before this call and is not running after it.</summary>
    public bool Killed { get; set; }
}

/// <summary>
/// WebSocket push frame on topic "conflicts". Sent each time the detected
/// set changes - an app appearing, leaving, or restarting under a new pid.
/// The list is the full current
/// snapshot - clients overwrite rather than diff.
/// </summary>
public sealed class ConflictsFrame
{
    public List<DetectedConflict> Conflicts { get; set; } = new();
}

/// <summary>
/// One <see cref="Nexus.Service.Conflicts.ConflictAppDefinition"/> as the
/// settings UI sees it. Process and service names stay server-side: the SPA
/// only ever addresses an app by Id.
/// </summary>
public sealed class ConflictCatalogApp
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Category { get; set; } = "";
}

/// <summary>Response for GET /conflicts/catalog - the full set of apps the startup shutdown can act on.</summary>
public sealed class GetConflictCatalogResponse
{
    public List<ConflictCatalogApp> Apps { get; set; } = new();
}
