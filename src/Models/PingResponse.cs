namespace Nexus.Service.Models;

public record PingResponse
{
    public string Service { get; init; } = "";
    public string Version { get; init; } = "";
    public bool Initialized { get; init; }
    /// <summary>"macos", "windows", or "linux" - lets the web UI conditionally render platform-specific settings.</summary>
    public string Platform { get; init; } = "";
    /// <summary>OS-reported computer name (Environment.MachineName). Surfaced in the panel tray so a paired phone can identify which PC it's connected to.</summary>
    public string MachineName { get; init; } = "";
}
