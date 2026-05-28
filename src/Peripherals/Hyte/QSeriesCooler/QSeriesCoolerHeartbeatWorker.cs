using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Nexus.Service.Peripherals.Hyte.QSeriesCooler;

/// <summary>
/// Background poller for the Q-series cooler. Keeps trying to open the COM
/// port until the cooler shows up, then reads the firmware version once on
/// first connect so the Firmware Updates page can show current-vs-available.
/// Mirrors <see cref="Nexus.Service.Peripherals.Hyte.MiniHub.MiniHubHeartbeatWorker"/>
/// minus the lighting/fan re-assertion (Q-series is read-only in v1).
/// </summary>
public sealed class QSeriesCoolerHeartbeatWorker : BackgroundService
{
    private readonly QSeriesCoolerHub _hub;

    public QSeriesCoolerHeartbeatWorker(QSeriesCoolerHub hub) { _hub = hub; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Console.Error.WriteLine("[qseries-cooler-heartbeat] ExecuteAsync started");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception ex) { Console.Error.WriteLine($"[qseries-cooler-heartbeat] tick exception: {ex.GetType().Name}: {ex.Message}"); }
            try { if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) break; }
            catch (OperationCanceledException) { break; }
        }
        _hub.Disconnect();
    }

    public void Tick()
    {
        var connectedBefore = _hub.IsConnected;
        if (!_hub.EnsureConnected()) return;
        if (!connectedBefore || string.IsNullOrEmpty(_hub.State.FirmwareVersion))
        {
            _hub.PollFirmwareVersion();
        }
    }
}
