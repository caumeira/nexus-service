using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Nexus.Service.Peripherals.Hyte.QSeriesCooler;

/// <summary>
/// Background poller for the Q-series cooler. Keeps trying to open the COM
/// port until the cooler shows up, then reads the firmware version once on
/// first connect so the Firmware Updates page can show current-vs-available.
/// Mirrors <see cref="Nexus.Service.Peripherals.Hyte.MiniHub.MiniHubHeartbeatWorker"/>.
/// Also nudges the Q-series lighting provider on (re)connect so the RgbBridge
/// rebuilds its frame map; the actual LED streaming is owned by
/// <see cref="Nexus.Service.Lighting.QSeriesLightingFrameWriter"/>.
/// </summary>
public sealed class QSeriesCoolerHeartbeatWorker : BackgroundService
{
    private readonly QSeriesCoolerHub _hub;
    private readonly Nexus.Service.Lighting.QSeriesLightingDeviceProvider? _lighting;

    public QSeriesCoolerHeartbeatWorker(QSeriesCoolerHub hub, Nexus.Service.Lighting.QSeriesLightingDeviceProvider? lighting = null)
    {
        _hub = hub;
        _lighting = lighting;
    }

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
        // Nudge the lighting provider so RgbBridge rebuilds its frame map on
        // (re)connect. Debounced inside the provider via a signature compare.
        _lighting?.OnHubStateUpdated();
    }
}
