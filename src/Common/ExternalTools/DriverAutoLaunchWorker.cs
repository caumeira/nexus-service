using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Platform;
using Nexus.Service.Widgets;

namespace Nexus.Service.Common.ExternalTools;

/// <summary>
/// Auto-launches the native driver of every installed first-party driver app whose
/// matching device is on the USB bus — so the iBUYPOWER AW5 cooler is driven at
/// service start, before any user logs in. Generic: it reads each app's
/// <c>driver</c> manifest block (only allowlisted apps have one — the registry
/// enforces that), so a future first-party driver app needs zero changes here.
///
/// Single-instance is the manager's guarantee; shutdown kill is the manager's
/// <see cref="ExternalToolManager.StopAsync"/>. This worker only starts things.
/// </summary>
public sealed class DriverAutoLaunchWorker : BackgroundService
{
    // After boot, not on the startup critical path. Re-checks periodically so a
    // hot-plugged device launches its driver without a restart.
    private const int InitialDelayMs = 1500;
    private const int TickPeriodMs = 5000;

    private readonly AppRegistry _apps;
    private readonly ExternalToolManager _tools;
    private readonly IUsbEnumerator _usb;

    public DriverAutoLaunchWorker(AppRegistry apps, ExternalToolManager tools, IUsbEnumerator usb)
    {
        _apps = apps;
        _tools = tools;
        _usb = usb;
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
        // Process teardown is ExternalToolManager.StopAsync (hosted) — not here.
    }

    /// <summary>One pass: launch the driver of each present, not-yet-running driver app.</summary>
    internal async Task RunOnceAsync(CancellationToken ct)
    {
        foreach (var entry in _apps.All())
        {
            var driver = entry.Manifest.Driver;
            if (driver is null) continue;

            // Already running → skip the USB scan entirely.
            if (_tools.GetStatus(driver.ToolId) == ToolStatus.Running) continue;

            // ResolvePresent reads the shared 10s-cached enumeration and returns null
            // when no matching device is attached — the silent "no hardware" gate.
            var spec = DriverToolSpecFactory.ResolvePresent(driver, entry.RootPath, _usb);
            if (spec is null) continue;

            await _tools.LaunchAsync(spec, ct);
        }
    }
}
