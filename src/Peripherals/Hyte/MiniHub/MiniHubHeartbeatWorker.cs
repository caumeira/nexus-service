using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Qos.Service.Peripherals.Hyte.MiniHub;

/// <summary>
/// Background poller for the MiniHub. Much simpler than NP50's heartbeat
/// because the MiniHub doesn't have a 5s revert-to-firmware timer and
/// has no per-port fan/temp poll in v1. The worker's job is just to
/// (a) keep trying to open the port until the device shows up, (b) read
/// the firmware version once on first connect, (c) re-assert software
/// RGB mode after a reconnect so our LED frames take effect.
/// </summary>
public sealed class MiniHubHeartbeatWorker : BackgroundService
{
    private readonly MiniHubHub _hub;
    private bool _modeAsserted;

    public MiniHubHeartbeatWorker(MiniHubHub hub) { _hub = hub; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception ex) { Console.Error.WriteLine($"[minihub-heartbeat] tick exception: {ex.GetType().Name}: {ex.Message}"); }
            try { if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) break; }
            catch (OperationCanceledException) { break; }
        }
        _hub.Disconnect();
    }

    public void Tick()
    {
        var connectedBefore = _hub.IsConnected;
        if (!_hub.EnsureConnected()) { _modeAsserted = false; return; }
        // First connect ⇒ read FW version + assert software RGB mode so
        // our lighting writes take effect. Cheap to re-assert on each
        // reconnect; no-op for steady-state.
        if (!connectedBefore || string.IsNullOrEmpty(_hub.State.FirmwareVersion))
        {
            _hub.PollFirmwareVersion();
        }
        if (!_modeAsserted)
        {
            if (_hub.SetRgbControlMode(MiniHubProtocol.RgbModeSoftware))
                _modeAsserted = true;
        }
    }
}
