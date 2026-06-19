using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Firmware;
using Nexus.Service.Models.Devices;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

/// <summary>
/// Firmware Updates endpoints. v1 is read-only: it reports, per connected
/// supported device, the version the device is running vs the newest version
/// bundled in this build (<see cref="BundledFirmwareCatalog"/>). The flash
/// action lands with the dfu-util flasher (see
/// <c>plans/firmware-flasher-tooling.md</c>).
/// </summary>
public static partial class DevicesRoutes
{
    private static void MapFirmwareEndpoints(WebApplication app)
    {
        // Only devices that are (a) connected and (b) have a bundled firmware
        // image are returned - no blank rows for devices we can't offer an
        // update for. CurrentVersion may still be empty when the device hasn't
        // reported its version yet; UpdateAvailable stays false in that case.
        app.MapGet("/devices/firmware/status", (DeviceManager dm, BundledFirmwareCatalog catalog, FirmwareFlasher flasher) =>
        {
            var result = new List<FirmwareStatusItem>();
            foreach (var d in dm.GetAll())
            {
                if (!d.Connected) continue;

                // FirmwareType is the catalog key: Id for most devices, the
                // connected variant ("q60"/"q80") for Q-series.
                var available = catalog.GetLatestVersion(d.FirmwareType);
                if (string.IsNullOrEmpty(available)) continue;

                result.Add(new FirmwareStatusItem
                {
                    DeviceType = d.Id,
                    FirmwareType = d.FirmwareType,
                    Name = d.Name,
                    Category = d.Category,
                    CurrentVersion = d.FirmwareVersion,
                    AvailableVersion = available,
                    UpdateAvailable = BundledFirmwareCatalog.IsNewer(available, d.FirmwareVersion),
                    AvailableVersions = catalog.GetAvailableVersions(d.FirmwareType).ToList(),
#if DEV_TOOLS
                    // Cross-branch / downgrade images for the dev-only picker.
                    // Absent from release builds so the UI can't offer them.
                    DevImages = flasher.FlashableImages(d.FirmwareType).ToList(),
#else
                    DevImages = new List<FlashableImage>(),
#endif
                });
            }
            return result;
        });

        // Start a flash (async). Body: { deviceType: <catalog key>, version }.
        // deviceType is the firmware-catalog key (the connected variant), i.e.
        // FirmwareStatusItem.FirmwareType - NOT the display id.
        app.MapPost("/devices/firmware/flash", (FlashRequest body, FirmwareFlasher flasher) =>
        {
            if (flasher.TryStart(body.DeviceType, body.Version, out var error))
                return Results.Json(new FlashStartResponse { Started = true }, AppJsonContext.Default.FlashStartResponse);
            return Results.Json(new FlashStartResponse { Error = true, Msg = error, Started = false },
                AppJsonContext.Default.FlashStartResponse, statusCode: StatusCodes.Status409Conflict);
        });

        // Poll flash progress. Global server-side state, so it survives the UI
        // navigating between tabs.
        app.MapGet("/devices/firmware/flash/status", (FirmwareFlasher flasher) => flasher.Status);
    }
}
