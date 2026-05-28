using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Nexus.Service.Peripherals.Hyte.Y70Display;

/// <summary>
/// Background poller for the Y70 display controller. Keeps trying to open the
/// COM port until it shows up, then reads the firmware version once on first
/// connect so the Firmware Updates page can show current-vs-available. Mirrors
/// <see cref="Nexus.Service.Peripherals.Hyte.QSeriesCooler.QSeriesCoolerHeartbeatWorker"/>.
/// </summary>
public sealed class Y70DisplayHeartbeatWorker : BackgroundService
{
    private readonly Y70DisplayHub _hub;

    public Y70DisplayHeartbeatWorker(Y70DisplayHub hub) { _hub = hub; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Console.Error.WriteLine("[y70-display-heartbeat] ExecuteAsync started");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception ex) { Console.Error.WriteLine($"[y70-display-heartbeat] tick exception: {ex.GetType().Name}: {ex.Message}"); }
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
