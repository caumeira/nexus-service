using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nexus.Service.Serialization;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// Written after UpdateDriverForPlugAndPlayDevices binds a PawnIO upgrade
/// that could not activate live (ERROR_SUCCESS_REBOOT_REQUIRED). Read back
/// on the next launch so EnsureInstalledAsync can tell "already staged,
/// waiting for a reboot" from "still needs an elevated upgrade run" without
/// re-prompting UAC every launch until the reboot happens.
/// </summary>
public sealed class PawnIoUpgradeMarker
{
    [JsonPropertyName("staged_version")] public string StagedVersion { get; set; } = "";
    [JsonPropertyName("staged_at_boot_time_utc")] public DateTime StagedAtBootTimeUtc { get; set; }
}

public static class PawnIoUpgradeMarkerStore
{
    public static string MarkerPath => PawnIoPaths.UpgradeMarkerPath;

    public static PawnIoUpgradeMarker? Read()
    {
        try
        {
            var path = MarkerPath;
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllBytes(path);
            return JsonSerializer.Deserialize(json, AppJsonContext.Default.PawnIoUpgradeMarker);
        }
        catch
        {
            return null;
        }
    }

    public static void Write(PawnIoUpgradeMarker marker)
    {
        try
        {
            var path = MarkerPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.SerializeToUtf8Bytes(marker, AppJsonContext.Default.PawnIoUpgradeMarker);
            File.WriteAllBytes(path, json);
        }
        catch { /* best-effort, worst case re-prompts once more */ }
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
        catch { /* best-effort */ }
    }
}
