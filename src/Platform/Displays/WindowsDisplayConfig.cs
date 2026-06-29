#if WINDOWS
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Maps each active GDI adapter name (\\.\DISPLAYn) to the monitor number the
/// Windows Settings display page shows. Since Win10/11 Settings numbers by
/// DISPLAYCONFIG source id + 1, not the \\.\DISPLAYn ordinal - the GDI
/// namespace keeps stale slots for detached outputs, so the two diverge.
/// </summary>
internal static class WindowsDisplayConfig
{
    /// <summary>
    /// gdi device name (\\.\DISPLAYn) -> Settings monitor number (sourceId + 1).
    /// Empty when QueryDisplayConfig is unavailable / fails, so callers fall
    /// back to the GDI ordinal.
    /// </summary>
    internal static IReadOnlyDictionary<string, int> SourceNumbersByGdiName()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != 0)
                return map;
            if (pathCount == 0) return map;

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
                return map;

            for (var i = 0; i < pathCount; i++)
            {
                var src = paths[i].sourceInfo;
                var name = GdiNameForSource(src.adapterId, src.id);
                if (string.IsNullOrEmpty(name)) continue;
                // sourceInfo.id is 0-based; Settings labels it id + 1. Clones
                // share a source id and so share a number, matching Settings.
                map[name] = (int)src.id + 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[displays-win] display-config numbering failed: {ex.Message}");
        }
        return map;
    }

    private static string GdiNameForSource(LUID adapterId, uint sourceId)
    {
        var query = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                adapterId = adapterId,
                id = sourceId,
            },
        };
        return DisplayConfigGetDeviceInfo(ref query) == 0 ? query.viewGdiDeviceName ?? "" : "";
    }

    // -- P/Invoke -----------------------------------------------------------

    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const int CCHDEVICENAME = 32;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public uint refreshRateNumerator;
        public uint refreshRateDenominator;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    // 64-byte ABI; the trailing union is opaque here - we never read modes,
    // QueryDisplayConfig only needs a correctly sized buffer.
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        public uint infoType;
        public uint id;
        public LUID adapterId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)] public string viewGdiDeviceName;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathInfoArray,
        ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);
}
#endif
