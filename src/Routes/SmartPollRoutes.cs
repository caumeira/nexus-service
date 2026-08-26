using Nexus.Service.Models;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors;

namespace Nexus.Service.Routes;

/// <summary>Drives the Settings > Monitoring per-drive SMART interval picker.</summary>
public static class SmartPollRoutes
{
    public static void MapSmartPollRoutes(this WebApplication app)
    {
        app.MapGet("/monitoring/smart-poll", (ISensorProvider sensors, IConfigStore config) =>
        {
            var monitoring = config.Load().Monitoring;
            var rotational = sensors.GetRotationalDrives();
            var drives = new List<SmartPollDriveDto>();

            // Reads the storage components the walk already populated; the interval
            // itself governs whether a SMART read happens, so opening this page
            // never pulls a drive off its configured cadence.
            foreach (var (componentId, component) in sensors.GetStorageComponents(includeSmart: true))
            {
                if (!LhmComponentIdentifiers.IsSmartStorageComponent(componentId)) continue;
                var id = LhmComponentIdentifiers.ToHardwareIdentifier(componentId);
                var spins = rotational.TryGetValue(id, out var isRotational) ? isRotational : (bool?)null;
                var configured = monitoring.SmartPollSeconds.TryGetValue(id, out var seconds);
                drives.Add(new SmartPollDriveDto
                {
                    Id = id,
                    Name = component.Name,
                    Seconds = configured ? seconds : SmartPollPolicy.DefaultSecondsFor(spins),
                    UsesDefault = !configured,
                    Rotational = spins,
                });
            }

            return Results.Ok(new SmartPollResponse
            {
                DefaultSeconds = monitoring.SmartPollDefaultSeconds,
                PerDrive = monitoring.SmartPollPerDrive,
                Choices = SmartPollPolicy.Choices,
                Drives = drives,
            });
        });
    }
}
