using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Nexus.Service.Peripherals.Hyte.Cnvs;

/// <summary>
/// Background worker whose only job is to make <see cref="CnvsHub"/> the
/// first claimant of the CNVS COM port at service startup, beating
/// OpenRGB-headless to it. Without this, OpenRGB launches via
/// <c>OpenRgbProcessManager.Start</c> within a couple of seconds of the
/// service coming up and grabs COM7 — every subsequent CnvsHub write
/// then loses the race and returns <c>UnauthorizedAccessException</c>.
///
/// The worker ticks every 5 s. On hot-plug (CNVS unplugged + replugged
/// during a session) the timer eventually re-grabs the port; we don't
/// need to be fast about it because the user-visible action that follows
/// (settings change, lighting tick) is rare enough that a 5 s window is
/// acceptable. If OpenRGB sneaks in first on a replug the user will see
/// LEDs go dark for one cycle until we win it back.
/// </summary>
public sealed class CnvsConnectionWorker : BackgroundService
{
    private const int InitialDelayMs = 100;
    private const int TickPeriodMs = 5000;

    private readonly CnvsHub _hub;

    public CnvsConnectionWorker(CnvsHub hub)
    {
        _hub = hub;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 100 ms head-start so we beat OpenRgbProcessManager.Start (which
        // runs from the same service-startup phase but doesn't kick in for
        // a few seconds after the hosted-service pipeline begins).
        try { await Task.Delay(InitialDelayMs, stoppingToken); } catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TickPeriodMs));
        try
        {
            do
            {
                try { _hub.EnsureConnected(); }
                catch (Exception ex) { Console.Error.WriteLine($"[cnvs-conn] tick failed: {ex.GetType().Name}: {ex.Message}"); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }

        _hub.Disconnect();
    }
}
