using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Handlers;
using Nexus.Service.Platform;

#if WINDOWS
using Microsoft.Win32;
#endif

namespace Nexus.Service.Peripherals.Aw5;

/// <summary>
/// Drives every AW5 pump display from live sensors, replacing the per-ODM vendor
/// driver .exe. Ticks at the Levelplay cycle rate, which that panel needs as a
/// keep-alive: it reverts to its own standalone reading a few seconds after frames
/// stop. CoolerMaster holds its last frame for about 30s and is written on the same
/// tick, well inside its own tolerance (its vendor sends every ~2.4s; any rate works).
///
/// Gated by Nexus Control on the aw5 handler, which is variant-agnostic: one toggle
/// covers both models. Gated off, Levelplay fades to standalone on its own and
/// CoolerMaster is blanked outright, since it would otherwise hold a stale reading.
///
/// Frames stop across the whole suspend/resume pair. A Levelplay SET_REPORT landing
/// while the host tears the USB stack down wedges the panel's control pipe: every
/// later one fails ERROR_GEN_FAILURE (31) on a freshly opened handle, and the devnode
/// is not re-created on resume, so the panel stays blank until the next suspend.
/// CoolerMaster is unaffected - it writes interrupt-OUT, not the control pipe.
/// </summary>
public sealed class Aw5PanelWorker : BackgroundService
{
    /// <summary>Off the startup critical path. An unsampled sensor stack reads as 0, which the panel renders harmlessly.</summary>
    private const int InitialDelayMs = 2000;

    /// <summary>
    /// Ticks between bus scans. Discovery walks every HID interface on the box, so
    /// doing it per tick would sweep the bus once a second forever, on machines with
    /// no AW5 too. The tick rate is the panel's keep-alive; hot-plug latency is not,
    /// and ~5s matches the other device connection workers.
    /// </summary>
    private const int RediscoverEveryTicks = 5;

    /// <summary>
    /// Quiet window after a resume: PowerModes.Resume fires while the USB stack is
    /// still rebuilding, which is the same hazard as writing into a collapsing one.
    /// </summary>
    private const int ResumeSettleMs = 3000;

    /// <summary>
    /// Consecutive refused cycles that mark a panel wedged. Above one so an unplug
    /// caught mid-cycle still self-heals on the next tick without a log line.
    /// </summary>
    private const int WedgedAfterFailures = 3;

    /// <summary>
    /// Ticks between retries once a panel is wedged: each refused cycle drops and
    /// reopens the handle, so the keep-alive rate costs one open per second forever.
    /// </summary>
    private const int WedgedRetryEveryTicks = 30;

    private readonly Aw5Hub _hub;
    private readonly Aw5SensorReader _reader;
    private readonly DeviceControlGate _gate;
    private bool _wasGatedOn;
    private int _loggedPanels = -1;
    private IReadOnlyList<Aw5PanelTarget> _panels = Array.Empty<Aw5PanelTarget>();
    private int _ticksSinceDiscover = int.MaxValue;

    /// <summary>Consecutive refused cycles per panel path, and the ticks left before a wedged one is retried.</summary>
    private readonly Dictionary<string, int> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _retryCountdown = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Blocks rendering from the moment the power hook runs, so a tick that starts mid-suspend writes nothing.</summary>
    private volatile bool _suspended;

    /// <summary>Tells the tick loop to close handles and clear health; set by both power legs, cleared by the tick that acts on it.</summary>
    private volatile bool _forgetPending;

    /// <summary>Armed before <see cref="_suspended"/> clears, so the settle window is already in force when rendering unblocks.</summary>
    private long _resumeSettleUntil;

    /// <summary>Cancels a cycle already in flight when the host suspends; replaced on resume and never disposed, since a tick can still hold the old token.</summary>
    private CancellationTokenSource _powerCts = new();

#if WINDOWS
    /// <summary>Guards the unsubscribe in StopAsync against a failed subscribe.</summary>
    private bool _powerEventsSubscribed;
#endif

    public Aw5PanelWorker(Aw5Hub hub, Aw5SensorReader reader, DeviceControlGate gate)
    {
        _hub = hub;
        _reader = reader;
        _gate = gate;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            try
            {
                SystemEvents.PowerModeChanged += OnPowerModeChanged;
                _powerEventsSubscribed = true;
            }
            catch (Exception ex)
            {
                ServiceLog.Info($"[aw5] failed to subscribe to power events: {ex.GetType().Name}: {ex.Message}");
            }
        }
#endif
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
#if WINDOWS
        if (_powerEventsSubscribed && OperatingSystem.IsWindows())
        {
            try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { /* already gone */ }
            _powerEventsSubscribed = false;
        }
#endif
        return base.StopAsync(cancellationToken);
    }

#if WINDOWS
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        // Suspend runs inline: the machine stops once every subscriber returns, so a
        // thread-pool handoff races the teardown this exists to stay ahead of. Cancel
        // resumes the awaiting render continuation on this pump thread, so a tick can
        // finish and close its handles here - bounded to that one continuation.
        if (e.Mode == PowerModes.Suspend) OnHostSuspending();
        else if (e.Mode == PowerModes.Resume) OnHostResumed();
    }
#endif

    /// <summary>
    /// Stops frames and cuts a cycle already in flight. No handle is closed under a
    /// thread mid-write: the cancelled render resumes as one continuation chain.
    /// </summary>
    internal void OnHostSuspending()
    {
        _suspended = true;
        _forgetPending = true;
        // Logged before the cancel, which can resume the render continuation on this
        // thread: the event line must precede whatever that continuation writes.
        ServiceLog.Info("[aw5] host suspending - frames stopped");
        Volatile.Read(ref _powerCts).Cancel();
    }

    /// <summary>
    /// Re-arms frames once the USB stack has had <paramref name="settleMs"/> to
    /// rebuild. Tests pass 0 rather than waiting out the real window.
    /// </summary>
    internal void OnHostResumed(int settleMs = ResumeSettleMs)
    {
        Volatile.Write(ref _resumeSettleUntil, Environment.TickCount64 + settleMs);
        Volatile.Write(ref _powerCts, new CancellationTokenSource());
        // The devnode usually survives a suspend, so CloseAbsent would keep the
        // pre-sleep handles; forgetting forces the first post-resume frame down a
        // handle opened against the rebuilt stack.
        _forgetPending = true;
        _suspended = false;
        ServiceLog.Info($"[aw5] host resumed - frames resume in {settleMs}ms");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(InitialDelayMs, stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Aw5Protocol.LevelplayCycleMs));
        try
        {
            do
            {
                try { await TickAsync(stoppingToken); }
                catch (Exception ex) { ServiceLog.Error($"[aw5] tick failed: {ex.GetType().Name}: {ex.Message}"); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }
        _hub.CloseAll();
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        // Ahead of the gate and the scan: nothing may reach the bus while the host
        // is going down.
        if (_forgetPending)
        {
            _forgetPending = false;
            ForgetPanels();
        }
        if (_suspended) return;
        if (Environment.TickCount64 < Volatile.Read(ref _resumeSettleUntil)) return;

        // Gate next: a bus scan is the expensive part of a tick, and a device the
        // user switched off needs neither the scan nor the frames.
        if (!_gate.IsEnabled(Aw5Handler.HandlerId))
        {
            // Only on the on->off edge: blanking every tick would spam a device the
            // user asked Nexus to leave alone.
            if (_wasGatedOn)
            {
                // A suspend/resume forgets the panels, so an off-edge landing inside
                // that window has nothing to blank; rescan rather than leave the
                // CoolerMaster holding a stale reading.
                foreach (var p in _panels.Count > 0 ? _panels : _hub.Discover()) _hub.Blank(p);
                ForgetPanels();
                _wasGatedOn = false;
            }
            return;
        }
        _wasGatedOn = true;

        // Tested before the increment: pre-incrementing the int.MaxValue seed
        // overflows to int.MinValue and the scan never runs at all.
        if (_ticksSinceDiscover >= RediscoverEveryTicks)
        {
            _ticksSinceDiscover = 0;
            _panels = _hub.Discover();
            _hub.CloseAbsent(_panels);
            PruneHealth(_panels);
            if (_panels.Count != _loggedPanels)
            {
                if (_panels.Count > 0) ServiceLog.Info($"[aw5] driving {_panels.Count} panel(s) natively");
                _loggedPanels = _panels.Count;
            }
        }
        _ticksSinceDiscover++;
        if (_panels.Count == 0) return;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Volatile.Read(ref _powerCts).Token);
        var reading = _reader.Read();
        for (var i = 0; i < _panels.Count; i++)
        {
            var panel = _panels[i];
            if (_retryCountdown.TryGetValue(panel.Path, out var wait) && wait > 0)
            {
                _retryCountdown[panel.Path] = wait - 1;
                continue;
            }

            var accepted = await _hub.RenderAsync(panel, reading, linked.Token);
            // A cancelled cycle is the host going down or the service stopping, not a
            // refusal; counting it would wedge-flag a healthy panel at every suspend.
            if (linked.IsCancellationRequested) return;
            if (accepted) NoteAccepted(panel);
            else NoteRefused(panel);
        }
    }

    private void NoteAccepted(Aw5PanelTarget panel)
    {
        if (_failures.Remove(panel.Path))
            ServiceLog.Info($"[aw5] {panel.Variant} panel is taking frames again");
        _retryCountdown.Remove(panel.Path);
    }

    private void NoteRefused(Aw5PanelTarget panel)
    {
        var failures = _failures.TryGetValue(panel.Path, out var prior) ? prior + 1 : 1;
        _failures[panel.Path] = failures;
        if (failures < WedgedAfterFailures) return;

        // Once per wedge, not per retry; NoteAccepted closes the pair.
        if (failures == WedgedAfterFailures)
            ServiceLog.Warn($"[aw5] {panel.Variant} panel refused {failures} cycles in a row - backing off to one attempt every {WedgedRetryEveryTicks} ticks");
        _retryCountdown[panel.Path] = WedgedRetryEveryTicks;
    }

    /// <summary>
    /// Drops health for paths no longer on the bus. Without it a panel that dropped
    /// out mid-cycle comes back on the same path still serving its stale backoff,
    /// blanking a healthy panel for a full retry window.
    /// </summary>
    private void PruneHealth(IReadOnlyList<Aw5PanelTarget> present)
    {
        if (_failures.Count == 0 && _retryCountdown.Count == 0) return;
        var live = present.Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _failures.Keys.ToArray())
            if (!live.Contains(path)) _failures.Remove(path);
        foreach (var path in _retryCountdown.Keys.ToArray())
            if (!live.Contains(path)) _retryCountdown.Remove(path);
    }

    /// <summary>Drops every handle and the per-panel health, so the next scan starts clean.</summary>
    private void ForgetPanels()
    {
        _hub.CloseAll();
        _panels = Array.Empty<Aw5PanelTarget>();
        _ticksSinceDiscover = int.MaxValue;
        _failures.Clear();
        _retryCountdown.Clear();
    }
}
