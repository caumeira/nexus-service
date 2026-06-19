using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nexus.Service.Serialization;

namespace Nexus.Service.Update;

/// <summary>
/// Marker file written before each install attempt so a failed apply can be
/// detected on the next boot without a manifest fetch.
/// </summary>
public sealed class StagedInstallMarker
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("installer_path")] public string InstallerPath { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    /// <summary>"pending" = downloaded+verified, not yet launched. "attempted" = installer was launched.</summary>
    [JsonPropertyName("state")] public string State { get; set; } = "pending";
    /// <summary>When true, the new instance opens the dashboard after confirming the version advanced.</summary>
    [JsonPropertyName("reopen_dashboard")] public bool ReopenDashboard { get; set; }
}

/// <summary>
/// Read/write/delete for the pending-install marker file at
/// <c>{StagingDir}/pending-install.json</c>.
/// </summary>
public static class StagedInstallMarkerStore
{
    public const string StatePending = "pending";
    public const string StateAttempted = "attempted";

    public static string MarkerPath =>
        Path.Combine(UpdateDownloader.StagingDir, "pending-install.json");

    public static StagedInstallMarker? Read()
    {
        try
        {
            var path = MarkerPath;
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllBytes(path);
            return JsonSerializer.Deserialize(json, AppJsonContext.Default.StagedInstallMarker);
        }
        catch
        {
            return null;
        }
    }

    public static void Write(StagedInstallMarker marker)
    {
        try
        {
            Directory.CreateDirectory(UpdateDownloader.StagingDir);
            var json = JsonSerializer.SerializeToUtf8Bytes(marker, AppJsonContext.Default.StagedInstallMarker);
            File.WriteAllBytes(MarkerPath, json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ota-marker] write failed: {ex.Message}");
        }
    }

    public static void Delete()
    {
        try
        {
            var path = MarkerPath;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ota-marker] delete failed: {ex.Message}");
        }
    }
}
