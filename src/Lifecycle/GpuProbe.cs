#if WINDOWS
using System;
using Microsoft.Win32;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// Diagnostic / building-block for the render-GPU selection: `--gpu-probe
/// [--set-pref N]` optionally writes this exe's DirectX GpuPreference class
/// (0 = clear/auto, 1 = power-saving/integrated, 2 = high-performance/discrete),
/// creates a GL context, prints the bound renderer, and exits. Meant to be run
/// as a throwaway subprocess: if the GL driver hangs, the parent kills this
/// process on timeout instead of wedging the service.
///
/// Output (stdout, one per line): optional SET_PREF=N, then RENDERER=&lt;name&gt;
/// on success (exit 0) or PROBE_FAILED / PROBE_EXCEPTION on failure (nonzero).
/// </summary>
internal static class GpuProbe
{
    public static int Run(string[] args)
    {
        int? setPref = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--set-pref", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], out var p))
            {
                setPref = p;
            }
        }

        if (setPref is int pref)
        {
            try
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                {
                    // S-1-5-18 = the LocalSystem hive, matching how the service
                    // (and GpuRenderPreference) read the per-exe preference.
                    using var key = Registry.Users.CreateSubKey(
                        @"S-1-5-18\Software\Microsoft\DirectX\UserGpuPreferences");
                    if (key is not null)
                    {
                        if (pref == 1 || pref == 2)
                            key.SetValue(exe, $"GpuPreference={pref};", RegistryValueKind.String);
                        else
                            key.DeleteValue(exe, throwOnMissingValue: false);
                    }
                }
                Console.WriteLine($"SET_PREF={pref}");
            }
            catch (Exception ex) { Console.WriteLine($"SET_PREF_FAILED={ex.Message}"); }
        }

        try
        {
            // Shorter than the service's own budget: this child only answers
            // "does this card hang?". GpuRenderSelect.ProbeWait outlasts it.
            var gpu = new Nexus.Service.Lighting.Engine.Gpu.GpuContext(160, 90)
            {
                InitTimeout = TimeSpan.FromSeconds(30),
            };
            lock (gpu.Lock)
            {
                gpu.EnsureInitializedLocked();
            }
            if (gpu.WaitForInit(gpu.InitTimeout))
            {
                Console.WriteLine($"RENDERER={gpu.Renderer}");
                return 0;
            }
            Console.WriteLine("PROBE_FAILED");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"PROBE_EXCEPTION={ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }
}
#endif
