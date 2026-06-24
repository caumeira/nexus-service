using System.Text.Json.Serialization;

namespace Nexus.Service.Models.Widgets;

public sealed class AppInstallStatusDto
{
    [JsonPropertyName("hasInstall")]
    public bool HasInstall { get; set; }

    [JsonPropertyName("target")]
    public string? Target { get; set; }

    [JsonPropertyName("devicePresent")]
    public bool DevicePresent { get; set; }

    [JsonPropertyName("installed")]
    public bool Installed { get; set; }

    [JsonPropertyName("installedVersion")]
    public string? InstalledVersion { get; set; }

    [JsonPropertyName("latestVersion")]
    public string? LatestVersion { get; set; }

    [JsonPropertyName("updateAvailable")]
    public bool UpdateAvailable { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = "notrunning";
}

public sealed class AppInstallTriggerDto
{
    [JsonPropertyName("started")]
    public bool Started { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}
