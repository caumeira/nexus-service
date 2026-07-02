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
/// discrete only if integrated's GL context creation hangs - the case behind
/// the customer whose Intel iGPU driver wedges context creation in session 0.
///
/// The "does this card hang?" test runs in a throwaway `--gpu-probe` subprocess:
/// a hang there is killed by us on timeout, so it never wedges the service (and
/// never triggers a restart - the beta.11 mistake). Because the DirectX
/// GpuPreference applies at context-creation time (verified: set-then-create
/// binds the chosen card in the same process, no restart), once a probe confirms
/// a card the service sets the preference and creates its own context directly
/// on it. The decision is persisted, so later boots skip probing and go straight
/// to the known-good card. There is no CPU fallback: if every card hangs the GPU
/// stays off (shader effects don't render; static/firmware lighting is fine).
/// </summary>
internal static class GpuRenderSelect
{
    private const string Unprobed = "unprobed";
    private const string Integrated = "integrated";
    private const string Discrete = "discrete";
    private const string Off = "off";

    // A working context inits in well under a second; a wedged one never
    // returns. A few seconds cleanly separates them, and the probe is a
    // subprocess off the critical path, so this is not boot latency.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

    // class -> the card it selects on a hybrid box.
    private const int ClassIntegrated = 0; // clear pref: Windows' default is the iGPU on a hybrid box
    private const int ClassDiscrete = 2;   // high-performance

    public static void SelectAndWarm(GpuContext gpu, Stopwatch sw)
    {
        // Only worth selecting when there's more than one card to choose. A
        // single-GPU box has nothing to fall back to: init directly.
        var usable = Nexus.Service.Sensors.GpuAdapterLuids.Enumerate().Count(a => a.VendorId != 0x1414);
        if (usable <= 1)
        {
            WarmDirect(gpu, sw, "single-gpu");
            return;
        }

        var state = LoadState();

        // Remembered a working card: go straight to it, no probe. If it has
        // since regressed (driver update / card removed) the direct init fails
        // safe (GPU off this session, service still up) and we re-probe next boot.
        if (state == Integrated || state == Discrete)
        {
            SetPref(state == Discrete ? ClassDiscrete : ClassIntegrated);
            WarmDirect(gpu, sw, $"remembered:{state}");
            if (!gpu.Available)
            {
                SaveState(Unprobed);
                GpuContext.Log("[gpu] select: remembered card no longer works; will re-probe next boot");
            }
            return;
        }

        // "off" is terminal (every card hung once). Stay off without re-probing
        // each boot; a reinstall or the render-GPU picker resets it.
        if (state == Off)
        {
            GpuContext.Log("[gpu] select: GPU rendering disabled by a prior probe (no card worked); leaving off");
            return;
        }

        // Unprobed (first run): probe integrated, then discrete, each isolated in
        // a subprocess so a hang can't wedge us.
        GpuContext.Log("[gpu] select: probing GPUs (first run)");
        if (ProbeCard(ClassIntegrated))
        {
            SaveState(Integrated);
            SetPref(ClassIntegrated);
            WarmDirect(gpu, sw, "probed:integrated");
            return;
        }
        GpuContext.Log("[gpu] select: integrated probe failed; trying discrete");
        if (ProbeCard(ClassDiscrete))
        {
            SaveState(Discrete);
            SetPref(ClassDiscrete);
            WarmDirect(gpu, sw, "probed:discrete");
            return;
        }
        SaveState(Off);
        GpuContext.Log("[gpu] select: no GPU produced a working context; GPU rendering off (no CPU fallback)");
    }

    private static void WarmDirect(GpuContext gpu, Stopwatch sw, string why)
    {
        GpuContext.Log($"[gpu] warmup: init ({why})");
        try
        {
            lock (gpu.Lock)
            {
                gpu.EnsureInitializedLocked();
            }
        }
        catch (Exception ex) { GpuContext.Log($"[gpu] warmup threw: {ex.Message}"); }
        GpuContext.Log(gpu.Available
            ? $"[gpu] warmup: GPU shader engine ready in {sw.ElapsedMilliseconds}ms on '{gpu.Renderer}'"
            : $"[gpu] warmup: GPU unavailable after {sw.ElapsedMilliseconds}ms (shader effects off; static/firmware lighting unaffected)");
    }

    // Spawn `Nexus.exe --gpu-probe --set-pref <cls>` and wait; kill on timeout
    // (a wedged driver). True iff it exited 0 (a context bound). WorkingDirectory
    // is pinned to the exe dir so the bundled GLFW native libs resolve (same
    // loader-search gotcha as the bundled adb).
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
            if (!p.WaitForExit((int)ProbeTimeout.TotalMilliseconds))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                GpuContext.Log($"[gpu] select: probe class {cls} timed out ({ProbeTimeout.TotalSeconds:0}s); killed");
                return false;
            }
            var ok = p.ExitCode == 0;
            var tail = p.StandardOutput.ReadToEnd().Trim().Replace('\n', ' ').Replace('\r', ' ');
            GpuContext.Log($"[gpu] select: probe class {cls} exit={p.ExitCode} [{tail}]");
            return ok;
        }
        catch (Exception ex)
        {
            GpuContext.Log($"[gpu] select: probe class {cls} spawn failed: {ex.Message}");
            return false;
        }
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

    private static string StatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Nexus", "gpu-render-state");

    private static string LoadState()
    {
        try { return File.Exists(StatePath) ? File.ReadAllText(StatePath).Trim() : Unprobed; }
        catch { return Unprobed; }
    }

    private static void SaveState(string state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, state);
        }
        catch { }
    }
}
#endif
