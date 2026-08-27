#if WINDOWS
using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Helper;
using Nexus.Service.Helper.Domains;
using Nexus.Service.Lifecycle;

namespace Nexus.Service.Activity;

/// <summary>
/// Tells the user-session helper whether anything still consumes its window-set
/// snapshot. Both consumers - the processes frame's App/Background split and
/// MonitoringEventCollector's app open/close events - stop when the Monitoring
/// gate is off, but the enumeration feeding them runs in another process and
/// used to continue regardless, so turning Monitoring off changed nothing about
/// the work being done.
///
/// FeatureGates reads the store live and raises no change event, so the gate is
/// polled. The interval only bounds how long an off-to-on flip takes to reach
/// the helper; the helper defaults to enumerating, so a service that never gets
/// here degrades to the old behaviour rather than a silently empty split.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowSetDemandService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly HelperRegistry _helper;
    private readonly FeatureGates _gates;
    private bool? _lastSent;

    public WindowSetDemandService(HelperRegistry helper, FeatureGates gates)
    {
        _helper = helper;
        _gates = gates;
        // A reconnecting helper starts with its own default (enumerating), and
        // the poll below only sends on change - without this, a helper that
        // reconnects while the gate is off would enumerate until the gate moved.
        _helper.Connected += _ => _lastSent = null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            do
            {
                try
                {
                    var wanted = _gates.Monitoring;
                    if (_lastSent != wanted && _helper.IsAnyConnected)
                    {
                        await WindowSetCommands.SetWantedAsync(_helper, wanted, stoppingToken).ConfigureAwait(false);
                        _lastSent = wanted;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[windowset-demand] pass failed: {ex.Message}");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }
}
#endif
