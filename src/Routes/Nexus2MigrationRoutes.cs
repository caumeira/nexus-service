using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Auth;
using Nexus.Service.Migration;
using Nexus.Service.Models;
using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

/// <summary>
/// Nexus 2 (legacy HYTE Nexus) returning-user welcome screen state
/// (dashboard-only, loopback). Detection runs fresh on every GET; the
/// offered flag latches only on dismiss, so a device that becomes eligible
/// later still triggers the screen on the next load.
///   GET  /migration/nexus2                   -> status
///   POST /migration/nexus2/dismiss            -> { dismissed: true }
///   POST /migration/nexus2/disable-autostart  -> ApiResponse
/// </summary>
internal static class Nexus2MigrationRoutes
{
    public static void MapNexus2MigrationEndpoints(this WebApplication app)
    {
        app.MapGet("/migration/nexus2", (IConfigStore store, INexus2Detector detector) =>
        {
            var settings = store.Load();
            var result = detector.Detect();
            var deviceEligible = IsDeviceEligible(settings);
            var pending = result.Detected && deviceEligible && !settings.Nexus2MigrationOffered;

            return Results.Json(new Nexus2MigrationStatusDto
            {
                Detected = result.Detected,
                ImportAvailable = result.ImportAvailable,
                DeviceEligible = deviceEligible,
                Version = result.Version,
                AutostartTaskPresent = result.AutostartTaskPresent,
                Pending = pending,
            }, AppJsonContext.Default.Nexus2MigrationStatusDto);
        }).LocalhostOnly();

        app.MapPost("/migration/nexus2/dismiss", (IConfigStore store) =>
        {
            store.Update(s => s.Nexus2MigrationOffered = true);
            return Results.Json(
                new Nexus2MigrationDismissResponse { Dismissed = true },
                AppJsonContext.Default.Nexus2MigrationDismissResponse);
        }).LocalhostOnly();

        app.MapPost("/migration/nexus2/disable-autostart", (INexus2Detector detector) =>
        {
            var response = detector.DisableAutostart()
                ? ApiResponse.Ok("Nexus 2 autostart task disabled")
                : ApiResponse.Fail("Could not disable the Nexus 2 autostart task");
            return Results.Json(response, AppJsonContext.Default.ApiResponse);
        }).LocalhostOnly();
    }

    private static bool IsDeviceEligible(NexusSettings settings)
    {
        foreach (var device in settings.PanelDevices.Values)
        {
            var surface = device.Capabilities?.Surface;
            if (surface == PanelSurfaces.Y70 || surface == PanelSurfaces.Q60)
            {
                return true;
            }
        }
        return false;
    }
}

public sealed class Nexus2MigrationStatusDto
{
    public bool Detected { get; set; }
    public bool ImportAvailable { get; set; }
    public bool DeviceEligible { get; set; }
    public string? Version { get; set; }
    public bool AutostartTaskPresent { get; set; }
    public bool Pending { get; set; }
}

public sealed class Nexus2MigrationDismissResponse
{
    public bool Dismissed { get; set; }
}
