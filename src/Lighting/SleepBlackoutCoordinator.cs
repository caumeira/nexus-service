using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Platform;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Fades lighting out as the host suspends or shuts down, and restores it on
/// resume, when <see cref="LightingSettings.SleepBlackout"/> is on.
///
/// Sleep already takes most devices dark for free, because the host cuts their
/// bus. RAM does not: DIMMs stay powered across S3 (and across S5 on a board
/// without ErP) and the SMBus controller holds whatever frame it was last
/// written, so the sticks glow all night. The fix is to write black BEFORE the
/// machine stops - after it stops there is no one left to write.
///
/// That deadline shapes the whole class. The OS gives suspend subscribers a
/// short window and then goes down regardless, so <see cref="OnSuspending"/>
/// runs INLINE on the caller's thread (handing off to the thread pool loses the
/// race) under a hard <see cref="Budget"/>, and every wait is on a real signal
/// - the engine publishing black, the OpenRGB writes completing - rather than a
/// guessed delay.
///
/// The fade is the one part that is time-based rather than signalled, so it is
/// deadline-first: it never eats into <see cref="TerminalReserve"/>, and the run
/// ALWAYS ends by engaging the hard blackout and pushing black, whether the fade
/// completed, was cut short, or never started. A fade that runs out of budget
/// leaves the lights dark, never parked half-lit.
/// </summary>
public sealed class SleepBlackoutCoordinator
{
    /// <summary>
    /// Total inline budget for a suspend. Windows proceeds with the suspend
    /// once handlers have had their turn, so overrunning this does not block
    /// sleep, it just stops being useful; the cap is what keeps a wedged
    /// OpenRGB socket from holding the shared SystemEvents pump thread.
    /// </summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Budget for the OS-shutdown path. Under the 1500ms cap on the fast
    /// teardown that calls it (Program.cs FastServiceShutdown), so the terminal
    /// black frame lands inside the cap instead of being cut off by it.
    /// </summary>
    internal static readonly TimeSpan ShutdownBudget = TimeSpan.FromMilliseconds(1200);

    /// <summary>
    /// Wall clock the fade ramps over when the budget allows it in full. Short
    /// on purpose: this is time the machine spends not-yet-suspending, and the
    /// longer it runs the more of it a slow OpenRGB write can strand mid-fade.
    /// </summary>
    internal static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(800);

    /// <summary>
    /// Tail of the budget the fade may not touch, kept for the terminal black
    /// frame. Sized for the engine publish plus an awaited push to every OpenRGB
    /// device, which is the leg that reaches RAM.
    /// </summary>
    private static readonly TimeSpan TerminalReserve = TimeSpan.FromMilliseconds(600);

    /// <summary>
    /// Interval between fade steps, roughly the engine's own frame rate. A
    /// cadence, not a wait for anything: there is no signal to ride, the fade is
    /// a function of elapsed time and the level is recomputed from the clock, so
    /// a late or coalesced step shortens the fade rather than skewing it.
    /// </summary>
    private static readonly TimeSpan FadeStepInterval = TimeSpan.FromMilliseconds(33);

    /// <summary>
    /// Share of the budget allowed for the engine to publish the final black
    /// frame. Bounded well under one budget so a stalled render loop still
    /// leaves time for the direct OpenRGB push.
    /// </summary>
    private static readonly TimeSpan EnginePublishBudget = TimeSpan.FromMilliseconds(300);

    private readonly LightingEngine _engine;
    private readonly IConfigStore _store;
    private readonly RgbBridge? _bridge;

    public SleepBlackoutCoordinator(LightingEngine engine, IConfigStore store, RgbBridge? bridge = null)
    {
        _engine = engine;
        _store = store;
        _bridge = bridge;
    }

    /// <summary>
    /// Call inline from the OS suspend notification. No-op when the setting is
    /// off. Swallows everything: a failure here must never abort the host's
    /// suspend handling.
    /// </summary>
    public void OnSuspending() => BlankOut(Budget, "suspend");

    /// <summary>
    /// Call inline from the service's fast teardown, on a real OS shutdown or
    /// restart only. A stop that is not the machine going down (tray quit,
    /// /service/stop, an OTA install, a restart for a GPU change) leaves the
    /// lighting alone: the user is not walking away from a dark machine, and the
    /// service is about to come back and repaint.
    /// </summary>
    public void OnHostShutdown() => BlankOut(ShutdownBudget, "shutdown");

    private void BlankOut(TimeSpan budget, string reason)
    {
        try
        {
            if (!_store.Load().Lighting.SleepBlackout)
            {
                return;
            }

            var deadline = DateTime.UtcNow + budget;
            // A hold already engaged (a second suspend notification, or a
            // shutdown that follows one) is dark or on its way there; re-running
            // the fade from full brightness would light the machine back up.
            var fade = _engine.Blackout ? "held" : FadeOut(deadline);

            // The terminal state, reached on every path: the hold at level 0, so
            // every later engine tick republishes black and any device that
            // arrives mid-suspend is blanked too.
            _engine.SetBlackout(true);

            // Every non-OpenRGB writer (NP50, Lian Li, Keeb, the hubs) polls the
            // engine's device frames on its own timer, so publishing black is
            // what hands them the blank frame to push.
            var published = _engine.WaitForBlackout(Clamp(EnginePublishBudget, deadline));

            var pushed = PushBridgeBlackout(deadline);
            ServiceLog.Info(
                $"[lighting-sleep] blanked for {reason} (fade={fade}, engine={(published ? "published" : "timeout")}, openrgb={pushed})");
        }
        catch (Exception ex)
        {
            ServiceLog.Info($"[lighting-sleep] blackout on {reason} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Call on resume. Safe to call unconditionally - releasing a blackout that
    /// was never engaged is a no-op, which is what keeps a setting toggled off
    /// mid-sleep from stranding the user dark.
    /// </summary>
    public void OnResumed()
    {
        try
        {
            if (!_engine.Blackout)
            {
                return;
            }
            _engine.SetBlackout(false);
            ServiceLog.Info("[lighting-sleep] released blackout after resume");
        }
        catch (Exception ex)
        {
            ServiceLog.Info($"[lighting-sleep] blackout release failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Ramps every device from what it is showing down to black, inline, and
    /// returns what happened for the log line. Stops at
    /// <paramref name="deadline"/> minus <see cref="TerminalReserve"/> whatever
    /// state it is in; the caller blacks out unconditionally afterwards, so a
    /// short fade is a cosmetic loss and nothing more.
    /// </summary>
    private string FadeOut(DateTime deadline)
    {
        var start = DateTime.UtcNow;
        var lastStepBy = deadline - TerminalReserve;
        var span = FadeDuration;
        if (start + span > lastStepBy)
        {
            span = lastStepBy - start;
        }
        if (span <= TimeSpan.Zero)
        {
            return "no-budget";
        }

        // Engages the hold at full brightness: from here the effect is frozen
        // and this loop is the only thing deciding how bright devices are.
        _engine.SetBlackoutLevel(1f);
        var snapshot = _bridge?.CaptureFadeSnapshot();

        var steps = 0;
        while (true)
        {
            var elapsed = DateTime.UtcNow - start;
            if (elapsed >= span)
            {
                break;
            }

            var t = 1f - (float)(elapsed.TotalMilliseconds / span.TotalMilliseconds);
            // Squared: the byte we write is roughly linear in emitted light but
            // perception is not, so a linear ramp reads as a hard drop that then
            // crawls. t*t is close enough to perceptually even over this span.
            var level = t * t;
            _engine.SetBlackoutLevel(level);
            if (snapshot is not null && !PushFadeStep(snapshot, level, lastStepBy))
            {
                // A dropped socket or a step that ran past the reserve: stop
                // pushing, let the engine keep fading the writers that poll it,
                // and leave the terminal blackout to reach OpenRGB.
                snapshot = null;
            }
            steps++;

            var remaining = span - (DateTime.UtcNow - start);
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }
            Thread.Sleep(remaining < FadeStepInterval ? remaining : FadeStepInterval);
        }
        return $"{steps}steps/{(int)span.TotalMilliseconds}ms";
    }

    private bool PushFadeStep(RgbFadeSnapshot snapshot, float level, DateTime lastStepBy)
    {
        if (_bridge is null)
        {
            return false;
        }
        var remaining = lastStepBy - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return false;
        }
        using var cts = new CancellationTokenSource(remaining);
        try
        {
            // Blocking on purpose, same as the terminal push: the machine stops
            // when this returns, so there is no later to continue on.
            _bridge.PushFadeStepAsync(snapshot, level, cts.Token).GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Drives the awaited OpenRGB blackout to completion or to the deadline.
    /// Returns what happened, for the log line - the caller has no recovery to
    /// perform either way.
    /// </summary>
    private string PushBridgeBlackout(DateTime deadline)
    {
        if (_bridge is null)
        {
            return "n/a";
        }
        var remaining = deadline - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return "no-budget";
        }
        using var cts = new CancellationTokenSource(remaining);
        try
        {
            // Blocking on purpose: the machine stops when this returns, so
            // there is no later to continue on.
            _bridge.BlackoutAsync(cts.Token).GetAwaiter().GetResult();
            return "ok";
        }
        catch (OperationCanceledException)
        {
            return "timeout";
        }
        catch (Exception ex)
        {
            return $"failed:{ex.GetType().Name}";
        }
    }

    private static TimeSpan Clamp(TimeSpan wanted, DateTime deadline)
    {
        var remaining = deadline - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }
        return remaining < wanted ? remaining : wanted;
    }
}
