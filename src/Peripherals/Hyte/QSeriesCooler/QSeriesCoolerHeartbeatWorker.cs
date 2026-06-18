using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Platform;

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
    private readonly HardwarePresence _presence;
    private readonly Nexus.Service.Lighting.QSeriesLightingDeviceProvider? _lighting;
    private bool _firstTick = true;

    public QSeriesCoolerHeartbeatWorker(QSeriesCoolerHub hub, HardwarePresence presence, Nexus.Service.Lighting.QSeriesLightingDeviceProvider? lighting = null)
    {
        _hub = hub;
        _presence = presence;
        _lighting = lighting;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Head-start: claim the COM port before OpenRGB launches (race-and-hold).
        try { await Task.Delay(InitialDelayMs, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception ex) { ServiceLog.Error($"[qseries-cooler-heartbeat] tick exception: {ex.GetType().Name}: {ex.Message}"); }
            try { if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) break; }
            catch (OperationCanceledException) { break; }
        }
        _hub.Disconnect();
    }

    public void Tick()
    {
        // First tick stays ungated so the race-and-hold above isn't delayed by a
        // cold USB-enumeration scan. After that, skip silently when disconnected
        // and no Q-series cooler is on the bus.
        if (!_firstTick && !_hub.IsConnected
            && !_presence.UsbPresent(QSeriesCoolerProtocol.VendorId, QSeriesCoolerProtocol.Q60ProductId, QSeriesCoolerProtocol.Q80ProductId))
        {
            return;
        }
        _firstTick = false;

        var connectedBefore = _hub.IsConnected;
        if (!_hub.EnsureConnected()) return;
        if (!connectedBefore || string.IsNullOrEmpty(_hub.State.FirmwareVersion))
        {
            _hub.PollFirmwareVersion();
        }
        // Refresh pump RPM so the cooling page's fan card shows live telemetry.
        _hub.PollTelemetry();
        // Nudge the lighting provider so RgbBridge rebuilds its frame map on
        // (re)connect. Debounced inside the provider via a signature compare.
        _lighting?.OnHubStateUpdated();
    }
}
