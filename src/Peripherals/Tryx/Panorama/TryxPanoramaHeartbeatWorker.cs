using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices;

namespace Nexus.Service.Peripherals.Tryx.Panorama;

/// <summary>
/// Background service that maintains the ~1 Hz session keep-alive the Panorama
/// firmware requires. 10 s of silence causes the device screen to enter standby.
/// Tick failures are caught and logged; they never propagate to stop the worker.
/// </summary>
public sealed class TryxPanoramaHeartbeatWorker : BackgroundService
{
    private readonly TryxPanoramaHub _hub;
    private readonly DeviceControlGate _gate;

    public TryxPanoramaHeartbeatWorker(TryxPanoramaHub hub, DeviceControlGate gate)
    {
        _hub = hub;
        _gate = gate;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[tryx-heartbeat] tick exception: {ex.GetType().Name}: {ex.Message}");
            }
            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        _hub.Disconnect();
    }

    private void Tick()
    {
        // Nexus Control gate takes priority: a toggled-off device must go fully
        // silent (transport closed), not just stop sending new commands.
        if (!_gate.IsEnabled("tryx"))
        {
            if (_hub.IsConnected) _hub.Disconnect();
            return;
        }

        if (!_hub.EnsureConnected()) return;
        _hub.SendConn();
        // Skip the sensor push during an import so it can't land between the
        // transport/transported frames; SendConn alone keeps the screen awake.
        if (!_hub.ImportInProgress) _hub.SendStateAll();
        _hub.State.LastFrameMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
