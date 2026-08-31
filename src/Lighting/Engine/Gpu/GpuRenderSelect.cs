#if WINDOWS
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>
/// Windows render-GPU selection for the deferred warmup, in "auto" mode on a
/// multi-GPU machine. Prefers the integrated card (low power) and falls back to
/// discrete only if integrated's GL context creation hangs or crashes - the
/// case behind the customer whose Intel iGPU driver wedges context creation in
/// session 0.
///
/// The "does this card hang?" test runs in a throwaway `--gpu-probe` subprocess:
/// a hang there is killed by us on timeout, so it never wedges the service (and
/// never triggers a restart - the beta.11 mistake). Because the DirectX
/// GpuPreference applies at context-creation time (verified: set-then-create
/// binds the chosen card in the same process, no restart), once a probe confirms
/// a card the service sets the preference and creates its own context directly
/// on it. The decision is persisted, so later boots skip probing and init the
/// remembered card directly (no probe cost in steady state).
///
/// A native fast-fail (0xc0000409) inside GL init cannot be caught by a managed
/// timeout - it kills the process. A crash-guard marker is written before every
/// direct init and cleared after it returns; if a boot finds the marker still
/// present, the previous boot crashed initializing that card, so it is demoted
/// and we re-probe rather than crash again (bounds a regressed card to one
/// crash, not an SCM restart loop). There is no CPU fallback: if every card
/// fails the GPU stays off (shader effects don't render; static/firmware
/// lighting is fine) and the service stays up.
/// </summary>
internal static class GpuRenderSelect
{
    private const string Unprobed = "unprobed";
    private const string Integrated = "integrated";
    private const string Discrete = "discrete";
    private const string Off = "off";

    // Parent's backstop wait for a probe child. Must outlast the budget the
    // child pins on itself (see GpuProbe) so a working-but-slow card finishes on
    // its own and the parent only kills a truly wedged child.
    private static readonly TimeSpan ProbeWait = TimeSpan.FromSeconds(35);

    private const int ClassIntegrated = 0; // clear pref: Windows' default is the iGPU on a hybrid box
    private const int ClassDiscrete = 2;   // high-performance

    public static void SelectAndWarm(GpuContext gpu, Stopwatch sw)
    {
        // A leftover crash-guard means the last boot's direct init of this card
        // crashed (native fast-fail) - demote it so we don't crash again.
        var crashed = ConsumeCrashGuard();

        var usable = Nexus.Service.Sensors.GpuAdapterLuids.Enumerate().Count(a => a.VendorId != 0x1414);
        if (usable <= 1)
        {
            // Single card: nothing to fall back to. Probe it (isolates a
            // hang/crash) then init; if it fails, off rather than crash-loop.
            if (crashed != null)
            {
                GpuContext.Log("[gpu] select: sole GPU crashed init last boot; leaving GPU off");
                SaveState(Off);
                return;
            }
            if (ProbeCard(ClassIntegrated)) { WarmGuarded(gpu, sw, Integrated, ClassIntegrated, "single-gpu"); }
            else { SaveState(Off); GpuContext.Log("[gpu] select: sole GPU probe failed; GPU off"); }
            return;
        }

        var state = LoadState();
        if (crashed != null && crashed == state)
        {
            GpuContext.Log($"[gpu] select: {state} crashed init last boot; demoting and re-probing");
            state = Unprobed;
        }

        // Remembered a working card: init it directly, no probe (fast steady
        // state). A hang fails safe (GPU off this session) and re-probes next
        // boot; a crash is caught by the crash-guard above.
        if (state == Integrated || state == Discrete)
        {
            WarmGuarded(gpu, sw, state, state == Discrete ? ClassDiscrete : ClassIntegrated, $"remembered:{state}");
            if (gpu.Failed)
            {
                SaveState(Unprobed);
                GpuContext.Log("[gpu] select: remembered card failed init; will re-probe next boot");
                TrySwitchToOtherCard(gpu, sw, state);
            }
            else if (!gpu.Available)
            {
                // Still initializing. NOT a verdict - it lands on its own and
                // Available flips. Demoting here would re-pay two probes on
                // every later boot for a card that works.
                GpuContext.Log("[gpu] select: remembered card still initializing; keeping it");
            }
            return;
        }

        // Terminal: every card failed a prior probe. Don't re-probe each boot
        // (two timeouts is slow); the render-GPU picker clears this to re-probe.
        if (state == Off)
        {
            GpuContext.Log("[gpu] select: GPU rendering disabled (no card produced a working context); pick a GPU to re-probe");
            return;
        }

        // Unprobed: probe integrated, then discrete, each isolated in a subprocess.
        GpuContext.Log("[gpu] select: probing GPUs (first run)");
        if (ProbeCard(ClassIntegrated)) { WarmGuarded(gpu, sw, Integrated, ClassIntegrated, "probed:integrated"); return; }
        GpuContext.Log("[gpu] select: integrated probe failed; trying discrete");
        if (ProbeCard(ClassDiscrete)) { WarmGuarded(gpu, sw, Discrete, ClassDiscrete, "probed:discrete"); return; }
        SaveState(Off);
        GpuContext.Log("[gpu] select: no GPU produced a working context; GPU rendering off (no CPU fallback)");
    }

    // Set the pref, drop a crash-guard, init in-process, clear the guard. A
    // native crash during init leaves the guard for the next boot to demote the
    // card. Persists the state only once the context is actually available.
    private static void WarmGuarded(GpuContext gpu, Stopwatch sw, string stateName, int cls, string why)
    {
        SetPref(cls);
        WriteCrashGuard(stateName);
        GpuContext.Log($"[gpu] warmup: init ({why})");
        try
        {
            lock (gpu.Lock)
            {
                gpu.EnsureInitializedLocked();
            }
            // Outside the lock: the render path takes it every frame.
            gpu.WaitForInit(gpu.InitTimeout);
        }
        catch (Exception ex) { GpuContext.Log($"[gpu] warmup threw: {ex.Message}"); }
        ClearCrashGuard();
        GpuContext.Log(gpu.Available
            ? $"[gpu] warmup: GPU shader engine ready in {sw.ElapsedMilliseconds}ms on '{gpu.Renderer}'"
            : $"[gpu] warmup: no GPU context after {sw.ElapsedMilliseconds}ms "
              + $"({(gpu.Failed ? "init failed" : "still initializing")}; every lighting mode is shader-rendered, so devices stay dark until it lands)");
        if (gpu.Available) SaveState(stateName);
    }

    // Spawn `Nexus.exe --gpu-probe --set-pref <cls>` and wait; kill on timeout (a
    // wedged driver). True iff it exited 0 (a context bound). Both output streams
    // are drained concurrently so a chatty child can't fill a pipe buffer and
    // deadlock. WorkingDirectory is pinned to the exe dir so the bundled GLFW
    // native libs resolve (same loader-search gotcha as the bundled adb).
    private static bool ProbeCard(int cls)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--gpu-probe");
            psi.ArgumentList.Add("--set-pref");
            psi.ArgumentList.Add(cls.ToString());
            using var p = Process.Start(psi);
            if (p is null) return false;
            // Drain both pipes concurrently to avoid a buffer-full deadlock.
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit((int)ProbeWait.TotalMilliseconds))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                GpuContext.Log($"[gpu] select: probe class {cls} timed out ({ProbeWait.TotalSeconds:0}s); killed");
                return false;
            }
            var ok = p.ExitCode == 0;
            var tail = (stdout.Result + " " + stderr.Result).Trim().Replace('\n', ' ').Replace('\r', ' ');
            GpuContext.Log($"[gpu] select: probe class {cls} exit={p.ExitCode} [{tail}]");
            return ok;
        }
        catch (Exception ex)
        {
            GpuContext.Log($"[gpu] select: probe class {cls} spawn failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Live fallback to the other card. Caller guarantees <paramref name="failedState"/>
    /// actually FAILED: only a terminated attempt can be swapped in-process
    /// (GpuContext.ResetForRetry), because a card that is merely slow is still
    /// inside GLFW init and a second init beside it is undefined.
    ///
    /// The retry runs through WarmGuarded like every other direct init, so it
    /// keeps the crash guard - the card has not been probed this boot, and a
    /// native fast-fail here would otherwise take the service down unrecorded.
    /// </summary>
    private static void TrySwitchToOtherCard(GpuContext gpu, Stopwatch sw, string failedState)
    {
        var other = failedState == Discrete ? Integrated : Discrete;
        if (!gpu.ResetForRetry())
        {
            GpuContext.Log($"[gpu] select: no in-process retry left; '{other}' waits for the next start");
            return;
        }
        GpuContext.Log($"[gpu] select: '{failedState}' failed init; retrying on '{other}' in-process");
        WarmGuarded(gpu, sw, other, other == Discrete ? ClassDiscrete : ClassIntegrated, $"switch-from:{failedState}");
        GpuContext.Log(gpu.Available
            ? $"[gpu] select: switched to '{other}' ({gpu.Renderer}) after {sw.ElapsedMilliseconds}ms, no restart needed"
            : $"[gpu] select: '{other}' did not produce a context either; GPU rendering off");
    }

    private static void SetPref(int cls)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            using var key = Registry.Users.CreateSubKey(
                @"S-1-5-18\Software\Microsoft\DirectX\UserGpuPreferences");
            if (key is null) return;
            if (cls == 1 || cls == 2) key.SetValue(exe, $"GpuPreference={cls};", RegistryValueKind.String);
            else key.DeleteValue(exe, throwOnMissingValue: false);
        }
        catch { }
    }

    private static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Nexus");

    private static string StatePath => Path.Combine(DataDir, "gpu-render-state");
    private static string CrashGuardPath => Path.Combine(DataDir, "gpu-init-crashguard");

    private static string LoadState()
    {
        try { return File.Exists(StatePath) ? File.ReadAllText(StatePath).Trim() : Unprobed; }
        catch { return Unprobed; }
    }

    private static void SaveState(string state)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(StatePath, state);
        }
        catch { }
    }

    /// Clears the persisted selection so the next auto boot re-probes. Called
    /// when the user changes the render-GPU choice (so "off" is recoverable).
    public static void ClearState()
    {
        try { if (File.Exists(StatePath)) File.Delete(StatePath); }
        catch { }
    }

    private static void WriteCrashGuard(string card)
    {
        try { Directory.CreateDirectory(DataDir); File.WriteAllText(CrashGuardPath, card); }
        catch { }
    }

    private static void ClearCrashGuard()
    {
        try { if (File.Exists(CrashGuardPath)) File.Delete(CrashGuardPath); }
        catch { }
    }

    // Read + delete the crash-guard: non-null iff the last boot crashed during a
    // direct init (the guard was written before init and never cleared).
    private static string? ConsumeCrashGuard()
    {
        try
        {
            if (!File.Exists(CrashGuardPath)) return null;
            var card = File.ReadAllText(CrashGuardPath).Trim();
            File.Delete(CrashGuardPath);
            return string.IsNullOrEmpty(card) ? null : card;
        }
        catch { return null; }
    }
}
#endif
