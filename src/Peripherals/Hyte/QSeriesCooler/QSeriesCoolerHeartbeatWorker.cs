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
    // Small head-start so we open + hold the cooler's COM port before OpenRGB
    // launches (OpenRgbProcessManager starts it a few seconds into the hosted-
    // service pipeline). Mirrors CnvsConnectionWorker's race-and-hold: once we
    // hold the port, OpenRGB's open fails and it can't drive the Q-series; the
    // composite then strips OpenRGB's inert zombie entry by COM port.
    private const int InitialDelayMs = 100;

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
        // Head-start: claim the COM port before OpenRGB launches (race-and-hold).
        try { await Task.Delay(InitialDelayMs, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
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
