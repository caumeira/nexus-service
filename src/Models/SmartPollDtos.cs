namespace Nexus.Service.Models;

public sealed class SmartPollDriveDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Seconds between SMART reads; 0 = never.</summary>
    public int Seconds { get; set; }
    public bool UsesDefault { get; set; }
    /// <summary>Null when the drive's media type could not be determined.</summary>
    public bool? Rotational { get; set; }
}

public sealed class SmartPollResponse
{
    public int DefaultSeconds { get; set; }
    public bool PerDrive { get; set; }
    public IReadOnlyList<int> Choices { get; set; } = Array.Empty<int>();
    public IReadOnlyList<SmartPollDriveDto> Drives { get; set; } = Array.Empty<SmartPollDriveDto>();
}
