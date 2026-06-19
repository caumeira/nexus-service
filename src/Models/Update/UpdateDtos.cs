namespace Nexus.Service.Models.Update;

// All field names match the frozen API contract in the OTA plan.
// Do NOT rename fields without coordinating with nexus-web.

/// <summary>GET /update/status response.</summary>
public sealed class UpdateStatusResponse
{
    public string CurrentVersion { get; set; } = "";
    public string LatestVersion { get; set; } = "";
    public bool UpdateAvailable { get; set; }
    /// <summary>"production" or "beta"</summary>
    public string Channel { get; set; } = "production";
    public string UpdateMode { get; set; } = "always";
    public string ReleaseNotes { get; set; } = "";
    public long LastCheckedUnix { get; set; }
    public string LastCheckError { get; set; } = "";
    /// <summary>"idle" | "checking" | "downloading" | "verifying" | "installing" | "ready" | "failed"</summary>
    public string State { get; set; } = "idle";
    [System.Text.Json.Serialization.JsonPropertyName("updateReady")]
    public bool UpdateReady { get; set; }
    /// <summary>
    /// Non-empty on the first GET /update/status after a successful update.
    /// Contains the new version string. Empty after the first read or 60s.
    /// </summary>
    public string JustUpdatedTo { get; set; } = "";
}

/// <summary>GET /update/progress response.</summary>
public sealed class UpdateProgressResponse
{
    public bool Active { get; set; }
    /// <summary>"idle" | "downloading" | "verifying" | "launching" | "installing" | "failed" | "done"</summary>
    public string Phase { get; set; } = "idle";
    public double Percent { get; set; }
    public string Message { get; set; } = "";
    public string Version { get; set; } = "";
    public bool Success { get; set; }
    public string Error { get; set; } = "";
}

/// <summary>POST /update/start response (200 or 409).</summary>
public sealed class UpdateStartResponse
{
    public bool Started { get; set; }
    public bool Error { get; set; }
    public string Msg { get; set; } = "";
}

/// <summary>POST /update/start request body.</summary>
public sealed class UpdateStartRequest
{
    /// <summary>Optional: require this version to be the one that installs.</summary>
    public string? Version { get; set; }
    /// <summary>When true, the dashboard is reopened after the install completes.</summary>
    public bool ReopenAfter { get; set; }
}
