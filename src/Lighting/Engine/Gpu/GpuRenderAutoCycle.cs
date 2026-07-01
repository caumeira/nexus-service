using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Lifecycle;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>
/// GPU shader-engine warmup, deferred off the startup-critical path (registered
/// on ApplicationStarted, run on a background thread) so a stalled GL driver
/// never blocks the SCM handshake - the boot-time Error 1053 / installer-stall
/// fix.
///
/// On Windows in "auto" render-GPU mode it also self-heals across cards: WGL
/// context creation can hang on a given GPU (a hybrid iGPU+dGPU box running as
/// the LocalSystem session-0 service has no interactive desktop to bind). The
/// only in-process retry after a hang would leak the wedged native thread, and
/// the DirectX GpuPreference is restart-to-apply, so the retry is out-of-process:
/// a tight per-attempt timeout, then set the next preference class and restart
/// (via the same FactoryReset finalizer /service/restart uses). First class that
/// yields a working context is pinned; if all fail the GPU stays off (there is
/// no CPU shader fallback - shader effects simply don't render). A machine whose
/// first attempt works pins immediately and never restarts.
/// </summary>
internal static class GpuRenderAutoCycle
{
    // Preference classes, in attempt order. 0 = auto (clear the pref, let
    // Windows pick), 2 = high-performance (discrete), 1 = power-saving
    // (integrated). Auto first so a healthy box pins on the first try.
    private static readonly int[] CandidateOrder = { 0, 2, 1 };

    // Per-attempt budget for the cycle: tight enough that a wedged context trips
    // and advances quickly, loose enough not to kill a slow-but-working first
    // init on cold hardware.
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(8);

    // The Exhausted-state re-probe is the last-chance recovery (runs once per
    // boot, never inside a restart cycle), so it gets a generous budget: a card
    // that only inits slowly still recovers on a later boot rather than staying
    // off forever.
    private static readonly TimeSpan ExhaustedReprobeTimeout = TimeSpan.FromSeconds(20);

    // Hard-exit backstop after StopApplication: a real driver hang can hold the
    // loader lock so a graceful shutdown never completes. The finalizer is
    // already spawned, so terminating is safe - it restarts us.
    private static readonly TimeSpan ShutdownWatchdog = TimeSpan.FromSeconds(10);

    private const string Probing = "probing";
    private const string Pinned = "pinned";
    private const string Exhausted = "exhausted";

    public static void Schedule(WebApplication app)
    {
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var t = new Thread(() => Run(app)) { IsBackground = true, Name = "nexus-gpu-warmup" };
            t.Start();
        });
    }

    private static void Run(WebApplication app)
    {
        var sw = Stopwatch.StartNew();
        GpuContext.Log("[gpu] warmup: starting (deferred, post-start)");

        GpuContext gpu;
        string userChoice;
        try
        {
            gpu = app.Services.GetRequiredService<GpuContext>();
            userChoice = app.Services.GetService<IConfigStore>()?.Load().Lighting.RenderGpu ?? "auto";
        }
        catch (Exception ex)
        {
            GpuContext.Log($"[gpu] warmup: could not resolve services: {ex.Message}");
            return;
        }

        // Auto-cycle is Windows-only (GpuPreference is a Windows mechanism) and
        // only runs in "auto": a user who explicitly picked a card owns that
        // choice, so we honor it with a single plain warmup and forget any cycle
        // progress.
        var auto = OperatingSystem.IsWindows()
            && string.Equals(userChoice, "auto", StringComparison.OrdinalIgnoreCase);
        if (!auto)
        {
            ClearState();
            WarmupOnce(gpu, sw, 0, TimeSpan.FromSeconds(10));
            return;
        }

        RunAutoCycle(app, gpu, sw);
    }

    private static void RunAutoCycle(WebApplication app, GpuContext gpu, Stopwatch sw)
    {
        var candidates = BuildCandidates();
        // A full sweep needs at most Count-1 restarts; the cap is an independent
        // backstop (in the persisted state, so it survives restarts) that forces
        // Exhausted even if the index logic or a flapping pin would otherwise
        // keep restarting.
        var maxRestarts = candidates.Count;
        var state = LoadState();

        // Terminal: every class already failed. Re-probe once at auto (generous
        // budget) in case the hardware/driver got fixed, but never restart here.
        if (state.Status == Exhausted)
        {
            GpuRenderPreference.ApplyClass(0);
            WarmupOnce(gpu, sw, 0, ExhaustedReprobeTimeout);
            if (gpu.Available)
            {
                SaveState(Pinned, 0, 0);
                GpuContext.Log("[gpu] auto-cycle: recovered on auto; pinned");
            }
            else
            {
                GpuContext.Log("[gpu] auto-cycle: all GPU preference classes still failing; GPU rendering stays off");
            }
            return;
        }

        var index = Math.Clamp(state.Index, 0, candidates.Count - 1);
        var cls = candidates[index];
        GpuRenderPreference.ApplyClass(cls);
        GpuContext.Log($"[gpu] auto-cycle: attempt index={index} pref-class={ClassName(cls)} (status={state.Status}, restarts={state.Restarts})");

        WarmupOnce(gpu, sw, cls, AttemptTimeout);
        if (gpu.Available)
        {
            SaveState(Pinned, index, 0); // clean pin resets the restart budget
            GpuContext.Log($"[gpu] auto-cycle: GPU ready on pref-class={ClassName(cls)}; pinned");
            return;
        }

        // A previously-working pin regressed (driver update / card removed):
        // re-probe from the top so we re-discover a good class.
        if (state.Status == Pinned && index != 0)
        {
            GpuRenderPreference.ApplyClass(candidates[0]);
            TryRestart(app, Probing, 0, state.Restarts + 1, maxRestarts,
                $"[gpu] auto-cycle: pinned pref-class={ClassName(cls)} regressed; re-probing from start, restarting");
            return;
        }

        var next = index + 1;
        if (next >= candidates.Count)
        {
            GpuRenderPreference.ApplyClass(0);
            SaveState(Exhausted, candidates.Count, state.Restarts);
            GpuContext.Log("[gpu] auto-cycle: no GPU preference class produced a working context; GPU rendering off (no CPU fallback)");
            return;
        }

        var nextCls = candidates[next];
        GpuRenderPreference.ApplyClass(nextCls);
        TryRestart(app, Probing, next, state.Restarts + 1, maxRestarts,
            $"[gpu] auto-cycle: pref-class={ClassName(cls)} failed; switching to pref-class={ClassName(nextCls)} and restarting");
    }

    // Persist the next step and restart - but only if the state actually wrote.
    // A silently-failed state write would make every boot re-read the same step
    // and restart forever; refusing to restart when we cannot record progress
    // (or when the cap is hit) turns that into a bounded "GPU off" instead.
    private static void TryRestart(WebApplication app, string status, int index, int restarts, int maxRestarts, string reason)
    {
        if (restarts > maxRestarts)
        {
            GpuRenderPreference.ApplyClass(0);
            SaveState(Exhausted, index, restarts);
            GpuContext.Log($"[gpu] auto-cycle: restart cap ({maxRestarts}) reached; GPU rendering off (no CPU fallback)");
            return;
        }
        if (!SaveState(status, index, restarts))
        {
            GpuContext.Log("[gpu] auto-cycle: could not persist cycle state; not restarting to avoid a loop, GPU rendering off");
            return;
        }
        GpuContext.Log(reason);
        Restart(app);
    }

    private static void WarmupOnce(GpuContext gpu, Stopwatch sw, int cls, TimeSpan timeout)
    {
        GpuContext.ActivePreferenceClass = cls;
        gpu.InitTimeout = timeout;
        try
        {
            lock (gpu.Lock)
            {
                gpu.EnsureInitializedLocked();
            }
            GpuContext.Log(gpu.Available
                ? $"[gpu] warmup: GPU shader engine ready in {sw.ElapsedMilliseconds}ms"
                : $"[gpu] warmup: GPU unavailable after {sw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            GpuContext.Log($"[gpu] warmup: threw after {sw.ElapsedMilliseconds}ms ({ex.GetType().Name}: {ex.Message})");
        }
    }

    // Only cycle preference classes when there is more than one physical card to
    // switch between; a single-GPU box just tries auto once.
    private static List<int> BuildCandidates()
    {
        var usable = Nexus.Service.Sensors.GpuAdapterLuids.Enumerate()
            .Count(a => a.VendorId != 0x1414); // exclude Microsoft (WARP/BSRD/virtual)
        return usable <= 1 ? new List<int> { 0 } : CandidateOrder.ToList();
    }

    private static string ClassName(int cls) => cls switch
    {
        1 => "1(integrated)",
        2 => "2(discrete)",
        _ => "0(auto)",
    };

    private static void Restart(WebApplication app)
    {
        try
        {
            // Spawn the detached finalizer first (it waits for our exit, then
            // sc-starts us), then stop, then a hard-exit backstop for a wedged
            // shutdown.
            FactoryReset.Begin(wipe: false);
            app.Lifetime.StopApplication();
            var watchdog = new Thread(() =>
            {
                Thread.Sleep(ShutdownWatchdog);
                GpuContext.Log("[gpu] auto-cycle: shutdown watchdog firing hard exit");
                HardExit();
            })
            { IsBackground = true, Name = "nexus-gpu-restart-watchdog" };
            watchdog.Start();
        }
        catch (Exception ex)
        {
            GpuContext.Log($"[gpu] auto-cycle: restart failed to initiate: {ex.Message}");
        }
    }

    private static void HardExit()
    {
#if WINDOWS
        try { TerminateProcess(GetCurrentProcess(), 0); return; }
        catch { }
#endif
        Environment.Exit(0);
    }

#if WINDOWS
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint exitCode);
#endif

    // State (Windows-only; a plain-text file in the Nexus data dir, no source-gen).

    private readonly record struct CycleState(string Status, int Index, int Restarts);

    private static string StatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Nexus", "gpu-render-state");

    private static CycleState LoadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return new CycleState(Probing, 0, 0);
            var status = Probing;
            var index = 0;
            var restarts = 0;
            foreach (var line in File.ReadAllLines(StatePath))
            {
                var kv = line.Split('=', 2);
                if (kv.Length != 2) continue;
                var k = kv[0].Trim();
                var v = kv[1].Trim();
                if (k == "status") status = v;
                else if (k == "index" && int.TryParse(v, out var i)) index = i;
                else if (k == "restarts" && int.TryParse(v, out var r)) restarts = r;
            }
            return new CycleState(status, index, restarts);
        }
        catch { return new CycleState(Probing, 0, 0); }
    }

    private static bool SaveState(string status, int index, int restarts)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, $"status={status}\nindex={index}\nrestarts={restarts}\n");
            return true;
        }
        catch { return false; }
    }

    private static void ClearState()
    {
        try { if (File.Exists(StatePath)) File.Delete(StatePath); }
        catch { }
    }
}
