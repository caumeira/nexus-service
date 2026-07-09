using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Platform;

namespace Nexus.Service.Diagnostics;

/// <summary>
/// Polls <see cref="DiagnosticsHealthModel"/> every 5 minutes (first check 2
/// minutes after start) and logs one warning line per "act" component, the
/// first time its id is seen this service run. A component id is never
/// re-alerted within the same run, even if it clears and re-triggers. Log
/// only: no tray/native notification is raised for a diagnostics issue.
/// </summary>
public sealed class DiagnosticsAlertService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    private readonly DiagnosticsHealthModel _health;
    private readonly HashSet<string> _alerted = new(StringComparer.Ordinal);

    public DiagnosticsAlertService(DiagnosticsHealthModel health)
    {
        _health = health;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try { Tick(); }
            catch (Exception ex) { ServiceLog.Warn($"[diagnostics-alert] tick failed: {ex.Message}"); }
        }
        while (await WaitForNextTickSafe(timer, stoppingToken));
    }

    private static async Task<bool> WaitForNextTickSafe(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
    }

    internal void Tick()
    {
        var health = _health.BuildHealth();
        foreach (var component in health.Components)
        {
            if (component.Status != HealthStatuses.Act)
            {
                continue;
            }
            if (!_alerted.Add(component.Id))
            {
                continue;
            }

            var topReason = component.Reasons.FirstOrDefault(r => r.Severity == HealthStatuses.Act)
                ?? component.Reasons.FirstOrDefault();
            var summary = topReason?.Summary ?? "no detail available";
            ServiceLog.Warn($"[diagnostics-alert] {component.Kind} issue: {component.Name} - {summary}");
        }
    }
}
