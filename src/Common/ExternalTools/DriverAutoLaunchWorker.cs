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
/// Keeps each installed bundled driver app's native driver running exactly while
/// its matching device is on the USB bus - so a device's driver runs at service
/// start, before any user logs in, and stops once the device leaves. Generic: it
/// reads each app's <c>driver</c> manifest block (only bundled apps keep one - the
/// registry enforces that), so a new driver app needs zero changes here.
///
/// A driver binds to the one device it was launched for, so an unplug (or a swap
/// to another variant) leaves the process driving nothing, or the wrong hardware:
/// the bus is the authority, and each tick reconciles against it.
///
/// Single-instance is the manager's guarantee; shutdown kill is
/// <see cref="ExternalToolManager.TerminateAll"/>, reached from the Windows
/// fast-shutdown path (Program.cs) as well as the hosted
/// <see cref="ExternalToolManager.StopAsync"/>.
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

    // Consecutive absent ticks before a running driver is stopped. Deliberately
    // generous: a premature kill costs a physical replug (bench-observed on the
    // AW5 - a killed driver then exits 259 on every relaunch until the machine
    // reboots), while a late one costs nothing, since the device is already gone.
    // A swap does not wait on this - it reconciles the moment the new variant
    // appears - so the margin is free.
    internal const int AbsentTicksBeforeStop = 6;

    private readonly AppRegistry _apps;
    private readonly ExternalToolManager _tools;
    private readonly IUsbEnumerator _usb;
    private readonly Func<DateTime> _utcNow;
    private readonly Dictionary<string, BackoffState> _backoff = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _launchedVariant = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _absentTicks = new(StringComparer.Ordinal);

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

    /// <summary>One pass: reconcile each driver app's tool with the device on the bus.</summary>
    internal async Task RunOnceAsync(CancellationToken ct)
    {
        foreach (var entry in _apps.All())
        {
            var driver = entry.Manifest.Driver;
            if (driver is null) continue;

            // Resolved every tick, before the running check, so the bus stays the
            // authority. Costs a read of the shared cached enumeration plus string
            // work; ResolvePresent returns null when no matching device is attached.
            var spec = DriverToolSpecFactory.ResolvePresent(driver, entry.RootPath, _usb);
            var running = _tools.GetStatus(driver.ToolId) == ToolStatus.Running;
            // Records the variant last attempted, so it outlives a dead process and a
            // later swap can still clear that variant's backoff.
            var swapped = spec is not null && RunningVariantDiffers(driver.ToolId, spec.Variant);

            // A scan that silently dropped a devnode returns a partial list that caches
            // as fresh, so one absent tick is not evidence the device left - and the
            // kill is unrecoverable for hardware that only re-attaches on a replug.
            // A real unplug stays absent, so it survives the streak.
            var absent = spec is null ? _absentTicks.GetValueOrDefault(driver.ToolId) + 1 : 0;
            if (spec is null) _absentTicks[driver.ToolId] = absent;
            else _absentTicks.Remove(driver.ToolId);

            if (running && (absent >= AbsentTicksBeforeStop || swapped))
            {
                _tools.Terminate(driver.ToolId);
                _launchedVariant.Remove(driver.ToolId);
                running = false;
            }

            // A different variant arrived: the outgoing device's failures say nothing
            // about this one, so it starts clean.
            if (swapped) _backoff.Remove(driver.ToolId);
            // The same device may come back. Retry the moment it does rather than
            // serving out a wait it already earned, but keep the failure count: a
            // device flapping between the same two states must still escalate.
            // Edge-triggered, so a 1-2 tick scan blip never reaches it.
            else if (absent == AbsentTicksBeforeStop) AllowImmediateRetry(driver.ToolId);

            if (spec is null) continue;

            if (running)
            {
                _backoff.Remove(driver.ToolId);
                continue;
            }

            if (_backoff.TryGetValue(driver.ToolId, out var state) && _utcNow() < state.NextAttemptUtc) continue;

            await _tools.LaunchAsync(spec, ct);
            _launchedVariant[driver.ToolId] = spec.Variant;

            // Unconditional: a tool that launched and stayed up clears this on the
            // next tick via the running check above. One that never resolves, or
            // that dies before the next tick, keeps escalating - so a crash-loop
            // backs off on the same schedule as a 404 with no second mechanism.
            NoteFailure(driver.ToolId);
        }
    }

    /// <summary>
    /// True when the tool was launched for a variant other than the one now on the
    /// bus. A tool this worker never launched has no recorded variant - a
    /// user-initiated install action starts one too - and counts as matching, so a
    /// variant mismatch never kills a process the worker cannot attribute. Absence
    /// still stops such a tool: nothing on the bus means nothing to drive.
    /// </summary>
    private bool RunningVariantDiffers(string toolId, string presentVariant)
        => _launchedVariant.TryGetValue(toolId, out var launched)
           && !string.Equals(launched, presentVariant, StringComparison.Ordinal);

    /// <summary>Drop the remaining wait but keep the failure count, so the next
    /// failure escalates from where this tool left off.</summary>
    private void AllowImmediateRetry(string toolId)
    {
        if (_backoff.TryGetValue(toolId, out var state)) state.NextAttemptUtc = DateTime.MinValue;
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
