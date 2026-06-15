using System;
using System.Collections.Generic;
using System.Globalization;
#if WINDOWS
using Vortice.DXGI;
#endif

namespace Nexus.Service.Sensors;

/// <summary>
/// Enumerates physical GPU adapters via DXGI to recover each adapter's LUID and
/// dedicated VRAM, so a GPU model (from LibreHardwareMonitor) can be matched to
/// the LUID that the PDH "GPU Engine" / "GPU Process Memory" per-process
/// counters tag their instances with. Windows-only; empty elsewhere, in which
/// case per-process GPU data stays attributed to no adapter (combined view).
/// </summary>
internal static class GpuAdapterLuids
{
    public readonly record struct Adapter(string Luid, string Description, uint VendorId, long DedicatedVramMb);

    public static IReadOnlyList<Adapter> Enumerate()
    {
#if WINDOWS
        var list = new List<Adapter>();
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            uint i = 0;
            while (factory.EnumAdapters1(i, out var adapter).Success)
            {
                try
                {
                    var d = adapter.Description1;
                    // Skip the Microsoft Basic Render Driver (software adapter).
                    if ((d.Flags & AdapterFlags.Software) == 0)
                    {
                        long vramMb = (long)((ulong)d.DedicatedVideoMemory / (1024UL * 1024UL));
                        list.Add(new Adapter(
                            $"{d.Luid.HighPart}:{d.Luid.LowPart}",
                            d.Description ?? "",
                            d.VendorId,
                            vramMb));
                    }
                }
                finally { adapter.Dispose(); }
                i++;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[gpu-luid] DXGI enumeration failed: {ex.Message}");
        }
        return list;
#else
        return Array.Empty<Adapter>();
#endif
    }

    /// <summary>
    /// Canonical LUID string ("HighPart:LowPart", decimal) from a PDH GPU-Engine
    /// instance's "luid_0xHIGH_0xLOW" hex pair. "" if not parseable. Matches the
    /// format produced from <see cref="Adapter.Luid"/> so the two compare equal.
    /// </summary>
    public static string LuidFromHex(string highHex, string lowHex)
    {
        if (TryHex(highHex, out var hi) && TryHex(lowHex, out var lo))
            return $"{(int)hi}:{lo}";
        return "";
    }

    private static bool TryHex(string s, out uint v)
    {
        v = 0;
        if (string.IsNullOrEmpty(s)) return false;
        var t = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s.AsSpan(2) : s.AsSpan();
        return uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
    }
}
