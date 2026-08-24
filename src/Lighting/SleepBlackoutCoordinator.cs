using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Platform;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Blanks lighting as the host suspends and restores it on resume, when
/// <see cref="LightingSettings.SleepBlackout"/> is on.
///
/// Sleep already takes most devices dark for free, because the host cuts their
/// bus. RAM does not: DIMMs stay powered across S3 and the SMBus controller
/// holds whatever frame it was last written, so the sticks glow all night. The
/// fix is to write black BEFORE the machine stops - after it stops there is no
/// one left to write.
///
/// That deadline shapes the whole class. The OS gives suspend subscribers a
/// short window and then goes down regardless, so <see cref="OnSuspending"/>
/// runs INLINE on the caller's thread (handing off to the thread pool loses the
/// race) under a hard <see cref="Budget"/>, and every wait is on a real signal
/// - the engine publishing black, the OpenRGB writes completing - rather than a
/// guessed delay.
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
    /// Share of <see cref="Budget"/> allowed for the engine to publish a black
    /// frame. Bounded well under one budget so a stalled render loop still
    /// leaves time for the direct OpenRGB push, which is the leg that reaches
    /// RAM - the device this exists for.
    /// </summary>
    private static readonly TimeSpan EnginePublishBudget = TimeSpan.FromMilliseconds(400);

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
    public void OnSuspending()
    {
        try
        {
            if (!_store.Load().Lighting.SleepBlackout)
            {
                return;
            }

            var deadline = DateTime.UtcNow + Budget;
            _engine.SetBlackout(true);

            // Every non-OpenRGB writer (NP50, Lian Li, Keeb, the hubs) polls the
            // engine's device frames on its own timer, so publishing black is
            // what hands them the blank frame to push.
            var published = _engine.WaitForBlackout(EnginePublishBudget);

            var pushed = PushBridgeBlackout(deadline);
            ServiceLog.Info(
                $"[lighting-sleep] blanked for suspend (engine={(published ? "published" : "timeout")}, openrgb={pushed})");
        }
        catch (Exception ex)
        {
            ServiceLog.Info($"[lighting-sleep] blackout on suspend failed: {ex.GetType().Name}: {ex.Message}");
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
}
