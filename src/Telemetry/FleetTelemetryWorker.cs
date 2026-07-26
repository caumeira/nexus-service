using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Nexus.Service.Telemetry;

/// <summary>
/// Runs <see cref="FleetEventService.RunPendingRetriesAsync"/> at boot and
/// every hour, so a delivery that failed (or a consent transition the process
/// crashed during) eventually reaches nexus-api. Mirrors HeartbeatService's
/// PeriodicTimer do/while shape.
/// </summary>
internal sealed class FleetTelemetryWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly FleetEventService _fleet;

    public FleetTelemetryWorker(FleetEventService fleet) => _fleet = fleet;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await _fleet.RunPendingRetriesAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[fleet-event] retry pass failed: {ex.GetType().Name}: {ex.Message}");
            }
        } while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
