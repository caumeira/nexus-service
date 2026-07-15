using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Platform;
using Nexus.Service.Widgets;

namespace Nexus.Service.Common.ExternalTools;

/// <summary>
/// Auto-launches the native driver of every installed bundled driver app whose
/// matching device is on the USB bus - so a device's driver runs at service start,
/// before any user logs in. Generic: it reads each app's <c>driver</c> manifest
/// block (only bundled apps keep one - the registry enforces that), so a new driver
/// app needs zero changes here.
///
/// Single-instance is the manager's guarantee; shutdown kill is
/// <see cref="ExternalToolManager.TerminateAll"/>, reached from the Windows
/// fast-shutdown path (Program.cs) as well as the hosted
/// <see cref="ExternalToolManager.StopAsync"/>. This worker only starts things.
/// </summary>
public sealed class DriverAutoLaunchWorker : BackgroundService
{
    // After boot, not on the startup critical path. Re-checks periodically so a
    // hot-plugged device launches its driver without a restart.
    private const int InitialDelayMs = 1500;
    private const int TickPeriodMs = 5000;

    // A driver whose payload never resolves - variant not published yet, CDN
    // outage - would otherwise re-fetch its manifest every tick forever. Back
    // off per tool, doubling from one tick up to this ceiling, and clear it once
    // the tool runs, so a late publish or a hot-plug still recovers unattended.
    private const int BackoffMaxMs = 300_000;

    private readonly AppRegistry _apps;
    private readonly ExternalToolManager _tools;
    private readonly IUsbEnumerator _usb;
    private readonly Func<DateTime> _utcNow;
    private readonly Dictionary<string, BackoffState> _backoff = new(StringComparer.Ordinal);

    public DriverAutoLaunchWorker(AppRegistry apps, ExternalToolManager tools, IUsbEnumerator usb)
        : this(apps, tools, usb, () => DateTime.UtcNow) { }

    /// <summary>Test seam: inject the clock the backoff schedule reads.</summary>
    internal DriverAutoLaunchWorker(AppRegistry apps, ExternalToolManager tools, IUsbEnumerator usb, Func<DateTime> utcNow)
    {
        _apps = apps;
        _tools = tools;
        _usb = usb;
        _utcNow = utcNow;
    }

    private sealed class BackoffState
    {
        public DateTime NextAttemptUtc;
        public int Failures;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(InitialDelayMs, stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TickPeriodMs));
        try
        {
            do
            {
                try { await RunOnceAsync(stoppingToken); }
                catch (Exception ex) { ServiceLog.Error($"[driver-autolaunch] tick failed: {ex.GetType().Name}: {ex.Message}"); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }
        // Process teardown is ExternalToolManager.TerminateAll - not here. On
        // Windows the hosted StopAsync never runs; Program.cs calls it directly.
    }

    /// <summary>One pass: launch the driver of each present, not-yet-running driver app.</summary>
    internal async Task RunOnceAsync(CancellationToken ct)
    {
        foreach (var entry in _apps.All())
        {
            var driver = entry.Manifest.Driver;
            if (driver is null) continue;

            // Already running → skip the USB scan entirely.
            if (_tools.GetStatus(driver.ToolId) == ToolStatus.Running)
            {
                _backoff.Remove(driver.ToolId);
                continue;
            }

            if (_backoff.TryGetValue(driver.ToolId, out var state) && _utcNow() < state.NextAttemptUtc) continue;

            // ResolvePresent reads the shared 10s-cached enumeration and returns null
            // when no matching device is attached - the silent "no hardware" gate.
            var spec = DriverToolSpecFactory.ResolvePresent(driver, entry.RootPath, _usb);
            if (spec is null) continue;

            await _tools.LaunchAsync(spec, ct);

            // Unconditional: a tool that launched and stayed up clears this on the
            // next tick via the Running check above. One that never resolves, or
            // that dies before the next tick, keeps escalating - so a crash-loop
            // backs off on the same schedule as a 404 with no second mechanism.
            NoteFailure(driver.ToolId);
        }
    }

    /// <summary>Push the tool's next attempt out, doubling per consecutive failure.</summary>
    private void NoteFailure(string toolId)
    {
        if (!_backoff.TryGetValue(toolId, out var state))
        {
            state = new BackoffState();
            _backoff[toolId] = state;
        }
        state.Failures++;
        // Failure 1 lands on the next tick (a transient blip costs no extra wait).
        var delayMs = Math.Min((long)TickPeriodMs << Math.Min(state.Failures - 1, 20), BackoffMaxMs);
        state.NextAttemptUtc = _utcNow().AddMilliseconds(delayMs);
    }
}
