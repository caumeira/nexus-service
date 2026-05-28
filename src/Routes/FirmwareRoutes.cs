using System.Collections.Generic;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Firmware;
using Nexus.Service.Models.Devices;

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
        // image are returned — no blank rows for devices we can't offer an
        // update for. CurrentVersion may still be empty when the device hasn't
        // reported its version yet; UpdateAvailable stays false in that case.
        app.MapGet("/devices/firmware/status", (DeviceManager dm, BundledFirmwareCatalog catalog) =>
        {
            var result = new List<FirmwareStatusItem>();
            foreach (var d in dm.GetAll())
            {
                if (!d.Connected) continue;

                var available = catalog.GetLatestVersion(d.Id);
                if (string.IsNullOrEmpty(available)) continue;

                result.Add(new FirmwareStatusItem
                {
                    DeviceType = d.Id,
                    Name = d.Name,
                    Category = d.Category,
                    CurrentVersion = d.FirmwareVersion,
                    AvailableVersion = available,
                    UpdateAvailable = BundledFirmwareCatalog.IsNewer(available, d.FirmwareVersion),
                });
            }
            return result;
        });
    }
}
