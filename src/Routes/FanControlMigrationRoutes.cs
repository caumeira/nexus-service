using System.Collections.Generic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Auth;
using Nexus.Service.Migration.FanControl;
using Nexus.Service.Models;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

/// <summary>
/// FanControl (Rem0o) detection and import. Detection runs fresh on every GET;
/// the offered flag latches only on dismiss, so a FanControl installed later
/// still triggers the onboarding screen. Loopback-only, like the Nexus 2
/// migration it mirrors: it reads another app's files and can close it.
///   GET  /migration/fancontrol                   -> status + config list
///   POST /migration/fancontrol/preview           -> what an import would do
///   POST /migration/fancontrol/apply             -> import the chosen categories
///   POST /migration/fancontrol/dismiss           -> stop offering the screen
///   POST /migration/fancontrol/close-app         -> both apps drive the same fans
///   POST /migration/fancontrol/disable-autostart
/// </summary>
internal static class FanControlMigrationRoutes
{
    public static void MapFanControlMigrationEndpoints(this WebApplication app)
    {
        app.MapGet("/migration/fancontrol", (IConfigStore store, IFanControlDetector detector) =>
        {
            var settings = store.Load();
            var result = detector.Detect();
            var dto = new FanControlStatusDto
            {
                Detected = result.Detected,
                Running = result.Running,
                ImportAvailable = result.ImportAvailable,
                AutostartPresent = result.AutostartPresent,
                Version = result.Version,
                InstallLocation = result.InstallLocation,
                Pending = result.Detected && result.ImportAvailable && !settings.FanControlImportOffered,
                Completed = settings.FanControlImportCompleted,
            };
            foreach (var c in result.Configs)
            {
                dto.Configs.Add(new FanControlConfigDto
                {
                    Path = c.Path,
                    Name = c.Name,
                    ModifiedUnixMs = c.ModifiedUnixMs,
                    IsDefault = c.IsDefault,
                });
            }
            return Results.Json(dto, AppJsonContext.Default.FanControlStatusDto);
        }).LocalhostOnly();

        app.MapPost("/migration/fancontrol/preview", (FanControlPreviewRequest body, FanControlImportService import) =>
            Results.Json(import.Preview(body.ConfigPath), AppJsonContext.Default.FanControlPreviewResponse)
        ).LocalhostOnly();

        app.MapPost("/migration/fancontrol/apply", (FanControlApplyRequest body, FanControlImportService import, Nexus.Service.Sockets.MultiplexHub hub) =>
        {
            var result = import.Apply(body.ConfigPath, body.Categories ?? new List<string>());
            if (!result.Error)
            {
                Nexus.Service.Sockets.PanelTopics.BroadcastCooling(hub);
            }
            return Results.Json(result, AppJsonContext.Default.FanControlApplyResponse);
        }).LocalhostOnly();

        app.MapPost("/migration/fancontrol/dismiss", (IConfigStore store) =>
        {
            store.Update(s => s.FanControlImportOffered = true);
            return Results.Json(
                new FanControlDismissResponse { Dismissed = true },
                AppJsonContext.Default.FanControlDismissResponse);
        }).LocalhostOnly();

        app.MapPost("/migration/fancontrol/close-app", async (IFanControlDetector detector) =>
        {
            var response = await detector.CloseAppAsync()
                ? ApiResponse.Ok("FanControl closed")
                : ApiResponse.Fail("Could not close FanControl");
            return Results.Json(response, AppJsonContext.Default.ApiResponse);
        }).LocalhostOnly();

        app.MapPost("/migration/fancontrol/disable-autostart", (IFanControlDetector detector) =>
        {
            var response = detector.DisableAutostart()
                ? ApiResponse.Ok("FanControl autostart disabled")
                : ApiResponse.Fail("Could not disable FanControl autostart");
            return Results.Json(response, AppJsonContext.Default.ApiResponse);
        }).LocalhostOnly();
    }
}

/// <summary>Status of a FanControl install and its importable configs.</summary>
public sealed class FanControlStatusDto
{
    public bool Detected { get; set; }
    public bool Running { get; set; }
    public bool ImportAvailable { get; set; }
    public bool AutostartPresent { get; set; }
    public string? Version { get; set; }
    public string? InstallLocation { get; set; }
    public List<FanControlConfigDto> Configs { get; set; } = new();
    /// <summary>True when the onboarding screen should offer the import: detected, importable, and not yet dismissed.</summary>
    public bool Pending { get; set; }
    /// <summary>True once any category has been imported. Survives an uninstall.</summary>
    public bool Completed { get; set; }
}

public sealed class FanControlConfigDto
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public long ModifiedUnixMs { get; set; }
    public bool IsDefault { get; set; }
}

/// <summary>One FanControl curve and what it maps to here.</summary>
public sealed class FanControlCurvePreviewDto
{
    public string Name { get; set; } = "";
    /// <summary>FanControl's curve kind: flat, graph, linear, mix, trigger, sync, auto.</summary>
    public string SourceKind { get; set; } = "";
    /// <summary>Nexus curve type it becomes, empty when unsupported.</summary>
    public string TargetType { get; set; } = "";
    public bool Supported { get; set; }
    /// <summary>Why it cannot be imported, when Supported is false.</summary>
    public string? Reason { get; set; }
    /// <summary>Local temperature source name it binds to, when it needs one.</summary>
    public string? SensorName { get; set; }
    /// <summary>Fans this curve will drive here.</summary>
    public List<string> FanNames { get; set; } = new();
}

/// <summary>One FanControl control and the local fan channel it matched.</summary>
public sealed class FanControlFanPreviewDto
{
    public string Identifier { get; set; } = "";
    public string SourceName { get; set; } = "";
    public string? ChannelId { get; set; }
    public string? ChannelName { get; set; }
    /// <summary>How the channel was matched: exact, normalized, name, or none.</summary>
    public string Match { get; set; } = "none";
    public string? NickName { get; set; }
    public bool HasCalibration { get; set; }
    public int? ManualDuty { get; set; }
    public int Offset { get; set; }
    public string? CurveName { get; set; }
}

public sealed class FanControlPreviewResponse
{
    public bool Available { get; set; }
    public string ConfigName { get; set; } = "";
    public int Version { get; set; }
    public List<FanControlCurvePreviewDto> Curves { get; set; } = new();
    public List<FanControlFanPreviewDto> Fans { get; set; } = new();
    /// <summary>Things deliberately left behind, phrased for the user.</summary>
    public List<string> Skipped { get; set; } = new();
    public int CurveCount { get; set; }
    public int CalibrationCount { get; set; }
    public int NameCount { get; set; }
    public int OffsetCount { get; set; }
    public int ManualCount { get; set; }
    public bool Error { get; set; }
    public string Msg { get; set; } = "Ok";
}

public sealed class FanControlApplyRequest
{
    public string ConfigPath { get; set; } = "";
    /// <summary>Any of: curves, calibration, names, offsets, manual.</summary>
    public List<string>? Categories { get; set; }
}

public sealed class FanControlApplyResponse
{
    public bool Error { get; set; }
    public string Msg { get; set; } = "Ok";
    public int CurvesImported { get; set; }
    public int CalibrationsImported { get; set; }
    public int NamesImported { get; set; }
    public int OffsetsImported { get; set; }
    public int ManualImported { get; set; }
}

public sealed class FanControlDismissResponse
{
    public bool Dismissed { get; set; }
}

public sealed class FanControlPreviewRequest
{
    /// <summary>Which saved config to read; empty means the default one.</summary>
    public string? ConfigPath { get; set; }
}
