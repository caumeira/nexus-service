using System;
using System.Collections.Generic;
using System.Globalization;
#if WINDOWS
using Vortice.DXGI;
using Vortice.Direct3D;
using Vortice.Direct3D11;
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

#if WINDOWS
    // Microsoft's PCI vendor id, used by the Basic Render Driver (WARP) and
    // indirect/virtual display adapters. Windows sometimes enumerates these
    // WITHOUT the AdapterFlags.Software flag, yet none provide an OpenGL ICD -
    // creating a GL context against one fail-fasts. No physical GPU vendor uses
    // it (NVIDIA 0x10DE, AMD 0x1002, Intel 0x8086).
    private const uint MicrosoftVendorId = 0x1414;
#endif

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
    /// True if at least one DXGI adapter can actually instantiate a Direct3D
    /// device. A removed GPU leaves its driver registered, so DXGI keeps
    /// enumerating it as a ghost adapter (no Software flag, real vendor id) -
    /// counting adapters can't tell a ghost from a present card. D3D11CreateDevice
    /// fails on the ghost (hardware absent) and succeeds on a real GPU, the same
    /// usability GLFW's WGL OpenGL context needs; without a real adapter, GLFW
    /// context creation fail-fasts inside the orphaned ICD (0xc0000409), which a
    /// managed catch can't intercept. Windows-only; false elsewhere.
    /// </summary>
    public static bool HasUsableHardwareGpu()
    {
#if WINDOWS
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            uint i = 0;
            while (factory.EnumAdapters1(i, out var adapter).Success)
            {
                try
                {
                    var d = adapter.Description1;
                    if ((d.Flags & AdapterFlags.Software) == 0 && d.VendorId != MicrosoftVendorId)
                    {
                        try
                        {
                            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown,
                                DeviceCreationFlags.None, null!, out var device, out _, out var ctx);
                            ctx?.Dispose();
                            device?.Dispose();
                            if (device is not null)
                                return true;
                        }
                        catch { } // adapter unusable (e.g. ghost of a removed card); try the next
                    }
                }
                finally { adapter.Dispose(); }
                i++;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[gpu-probe] D3D11 usability probe failed: {ex.Message}");
        }
        return false;
#else
        return false;
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
