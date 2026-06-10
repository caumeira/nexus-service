using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Cooling;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Hyte.SmartHub;

/// <summary>
/// Background poller for the SmartHub. Mirrors
/// <see cref="Nexus.Service.Peripherals.Hyte.MiniHub.MiniHubHeartbeatWorker"/>.
/// Jobs: (a) keep trying to open the port until the device shows up,
/// (b) read the firmware version once on first connect, (c) turn the hub's
/// onboard LED animation OFF after a (re)connect so our software LED frames
/// take effect, (d) poll the per-channel tach + enabled state so the cooling
/// page shows live RPM and only surfaces populated ports, (e) assert an
/// initial fan duty per (re)connect — the firmware powers up at full speed
/// and has no onboard curve, so an undriven port runs max forever.
/// </summary>
public sealed class SmartHubHeartbeatWorker : BackgroundService
{
    /// <summary>
    /// Duty written to ports with no saved manual speed and no curve binding.
    /// The hub's power-on default is 100% (the legacy agent's
    /// <c>SetInitialFanSpeed</c> leans on that for max-RPM calibration);
    /// without this write a fresh out-of-box port screams at full speed.
    /// </summary>
    private const int DefaultDutyPercent = 50;

    private readonly SmartHubHub _hub;
    private readonly HardwarePresence _presence;
    private readonly IConfigStore _config;
    private readonly SmartHubCoolingProvider _cooling;
    private bool _animationOffAsserted;
    private bool _initialDutyAsserted;
    private int _tickCount;
    private const int TraceEveryNTicks = 15; // every ~30 s with the 2 s timer

    public SmartHubHeartbeatWorker(SmartHubHub hub, HardwarePresence presence, IConfigStore config, SmartHubCoolingProvider cooling)
    {
        _hub = hub;
        _presence = presence;
        _config = config;
        _cooling = cooling;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
        // Skip silently when disconnected and no SmartHub is on the bus; stay live
        // while connected so an unplug is still handled.
        if (!_hub.IsConnected && !_presence.UsbPresent(SmartHubProtocol.VendorId, SmartHubProtocol.ProductId))
            return;

        var connectedBefore = _hub.IsConnected;
        if (!_hub.EnsureConnected()) { _animationOffAsserted = false; _initialDutyAsserted = false; return; }
        if (!connectedBefore) _initialDutyAsserted = false; // hub may have power-cycled back to 100%

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

        if (!_initialDutyAsserted)
            _initialDutyAsserted = ApplyInitialDuty();

        var pollOk = _hub.PollChannelInfo();
        if (++_tickCount % TraceEveryNTicks == 1)
        {
            var s = _hub.State;
            ServiceLog.Info(
                $"[smarthub-cooling] poll#{_tickCount} ok={pollOk} serial={s.Serial} " +
                $"fans=[{string.Join(",", FanSummaries(s))}]");
        }
    }

    /// <summary>
    /// Bring every PWM port off the firmware's 100% power-on default: restore
    /// the user's saved manual speed where one exists (so it survives a service
    /// restart), otherwise write <see cref="DefaultDutyPercent"/>. Curve-bound
    /// ports are skipped — <see cref="CurveEngine"/> drives those within a tick.
    /// Returns false (retry next tick) if any write fails.
    /// </summary>
    private bool ApplyInitialDuty()
    {
        var serial = _hub.State.Serial;
        if (string.IsNullOrEmpty(serial)) return false;

        var manual = _config.Load().Cooling.ManualSpeeds;
        var curveBound = CurveModes.BoundFanIds(_config);
        var ok = true;
        var restored = 0;
        for (var ch = 0; ch < SmartHubProtocol.FanChannelCount; ch++)
        {
            var id = SmartHubCoolingProvider.FanId(serial, ch);
            var hasSaved = manual.TryGetValue(id, out var saved);
            // A saved manual speed or a curve binding means the user set this
            // port up — seed the presence latch so a fan parked at 0% duty
            // doesn't vanish from the cooling page across a restart (the tach
            // reads 0 and would never latch on its own).
            if (hasSaved || curveBound.Contains(id)) _hub.State.Fans[ch].SeenFan = true;
            if (curveBound.Contains(id)) continue; // CurveEngine drives these within a tick
            if (hasSaved)
            {
                if (_cooling.RestoreManualSpeed(id, saved)) restored++;
                else ok = false;
            }
            else
            {
                ok &= _hub.WriteFanSpeed(ch, DefaultDutyPercent);
            }
        }
        if (ok) ServiceLog.Info($"[smarthub-cooling] initial duty asserted (default={DefaultDutyPercent}%, restored {restored} saved manual speed(s))");
        return ok;
    }

    private static System.Collections.Generic.IEnumerable<string> FanSummaries(SmartHubState s)
    {
        foreach (var f in s.Fans)
            yield return $"ch{f.Index}:en={f.Enabled},rpm={f.Rpm},duty={f.Duty}";
    }
}
