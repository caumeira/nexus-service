using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Persistence;

namespace Nexus.Service.Telemetry;

/// <summary>
/// Drains the in-memory telemetry queue and ships batches to every configured
/// sink. Mirrors HeartbeatService: post-boot, off the critical path, gated by
/// the same opt-out, and a failed flush never takes the worker down. Stays
/// dormant when no sink is configured (e.g. no PostHog key), so an unconfigured
/// build pays nothing.
/// </summary>
internal sealed class TelemetryFlushService : BackgroundService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(30);
    private const int MaxBatch = 100;

    private readonly TelemetryClient _telemetry;
    private readonly IConfigStore _store;
    private readonly IReadOnlyList<ITelemetrySink> _sinks;

    public TelemetryFlushService(TelemetryClient telemetry, IConfigStore store, IEnumerable<ITelemetrySink> sinks)
    {
        _telemetry = telemetry;
        _store = store;
        _sinks = sinks.Where(s => s.Enabled).ToArray();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_sinks.Count == 0)
            return; // nothing configured to send to - don't spin a timer.

        using var timer = new PeriodicTimer(FlushInterval);
        do
        {
            try
            {
                if (!Nexus.Service.FocusModes.FocusNetworkGate.IsHeld)
                    await FlushAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[telemetry] flush failed: {ex.GetType().Name}: {ex.Message}");
            }
        } while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        // Resolve the install id (and honor opt-out) once per flush.
        var distinctId = InstallIdentity.Resolve(_store);
        if (distinctId is null)
        {
            _telemetry.Clear(); // opted out - drop whatever queued.
            return;
        }

        List<TelemetryEvent> batch;
        while ((batch = _telemetry.DrainBatch(MaxBatch)).Count > 0)
        {
            foreach (var sink in _sinks)
                await sink.SendAsync(distinctId, batch, ct).ConfigureAwait(false);
        }
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
