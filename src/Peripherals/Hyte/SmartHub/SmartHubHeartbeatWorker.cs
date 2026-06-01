using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Nexus.Service.Peripherals.Hyte.SmartHub;

/// <summary>
/// Background poller for the Smart Hub. Mirrors
/// <see cref="Nexus.Service.Peripherals.Hyte.MiniHub.MiniHubHeartbeatWorker"/>.
/// Jobs: (a) keep trying to open the port until the device shows up,
/// (b) read the firmware version once on first connect, (c) turn the hub's
/// onboard LED animation OFF after a (re)connect so our software LED frames
/// take effect, (d) poll the per-channel tach + enabled state so the cooling
/// page shows live RPM and only surfaces populated ports.
/// </summary>
public sealed class SmartHubHeartbeatWorker : BackgroundService
{
    private readonly SmartHubHub _hub;
    private bool _animationOffAsserted;
    private int _tickCount;
    private const int TraceEveryNTicks = 15; // every ~30 s with the 2 s timer

    public SmartHubHeartbeatWorker(SmartHubHub hub) { _hub = hub; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Console.Error.WriteLine("[smarthub-heartbeat] ExecuteAsync started");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception ex) { Console.Error.WriteLine($"[smarthub-heartbeat] tick exception: {ex.GetType().Name}: {ex.Message}"); }
            try { if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) break; }
            catch (OperationCanceledException) { break; }
        }
        _hub.Disconnect();
    }

    public void Tick()
    {
        var connectedBefore = _hub.IsConnected;
        if (!_hub.EnsureConnected()) { _animationOffAsserted = false; return; }

        // First connect ⇒ read FW version. Cheap; no-op once populated.
        if (!connectedBefore || string.IsNullOrEmpty(_hub.State.FirmwareVersion))
        {
            _hub.PollFirmwareVersion();
        }

        // Hand the ARGB ports to software streaming by turning the onboard
        // animation off. Re-asserted on each reconnect; no-op for steady state.
        if (!_animationOffAsserted)
        {
            if (_hub.SetFirmwareAnimation(on: false))
                _animationOffAsserted = true;
        }

        var pollOk = _hub.PollChannelInfo();
        if (++_tickCount % TraceEveryNTicks == 1)
        {
            var s = _hub.State;
            Console.Error.WriteLine(
                $"[smarthub-cooling] poll#{_tickCount} ok={pollOk} serial={s.Serial} " +
                $"fans=[{string.Join(",", FanSummaries(s))}]");
        }
    }

    private static System.Collections.Generic.IEnumerable<string> FanSummaries(SmartHubState s)
    {
        foreach (var f in s.Fans)
            yield return $"ch{f.Index}:en={f.Enabled},rpm={f.Rpm},duty={f.Duty}";
    }
}
