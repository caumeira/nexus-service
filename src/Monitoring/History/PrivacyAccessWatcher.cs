using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lifecycle;
using Nexus.Service.Platform;

namespace Nexus.Service.Monitoring.History;

/// <summary>
/// Polls CapabilityAccessManager\ConsentStore for app privacy-capability
/// access sessions. A standalone BackgroundService rather than a slow
/// sub-cadence inside MetricsSampler's tick: MetricsSampler is cross-platform
/// 1Hz system-metrics sampling, while this is a Windows-only, much slower,
/// unrelated domain (registry access-log polling, not hardware telemetry) -
/// folding it in would put a platform gate inside a currently
/// platform-agnostic tick loop for no shared resource benefit.
/// </summary>
public sealed class PrivacyAccessWatcher : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan WarnThrottle = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

    private readonly IPrivacyAccessRegistryReader _reader;
    private readonly IPrivacySessionStore _store;
    private readonly PrivacyAccessTransitions _transitions = new();
    private readonly FeatureGates _gates;

    private DateTime _lastPruneUtc = DateTime.MinValue;
    private DateTime _lastWarnUtc = DateTime.MinValue;

    public PrivacyAccessWatcher(IPrivacyAccessRegistryReader reader, IPrivacySessionStore store, FeatureGates? gates = null)
    {
        _reader = reader;
        _store = store;
        _gates = gates ?? FeatureGates.AllEnabled;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                Tick(DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                MaybeWarn(ex);
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

    internal void Tick(DateTime nowUtc)
    {
        if (!_gates.Monitoring)
        {
            return;
        }
        var snapshot = _reader.ReadAll();
        var updates = _transitions.Advance(snapshot, new DateTimeOffset(nowUtc).ToUnixTimeSeconds());
        foreach (var update in updates)
        {
            _store.Upsert(update.Capability, update.AppId, update.StartUtcSec, update.EndUtcSec);
        }

        if (nowUtc - _lastPruneUtc < PruneInterval)
        {
            return;
        }
        _lastPruneUtc = nowUtc;
        try
        {
            var cutoffSec = new DateTimeOffset(nowUtc).ToUnixTimeSeconds() - PrivacyAccess.RetentionDays * 86_400L;
            _store.PruneOlderThan(cutoffSec);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[privacy-watcher] prune failed: {ex.Message}");
        }
    }

    private void MaybeWarn(Exception ex)
    {
        var now = DateTime.UtcNow;
        if (now - _lastWarnUtc < WarnThrottle)
        {
            return;
        }
        _lastWarnUtc = now;
        ServiceLog.Warn($"[privacy-watcher] tick failed: {ex.Message}");
    }
}
