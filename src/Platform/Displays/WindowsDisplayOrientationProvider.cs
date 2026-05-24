#if WINDOWS
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Rotates the HYTE Y70 panel via <c>ChangeDisplaySettingsEx</c>. Runs inside
/// the user-session helper so the change takes effect on the user's desktop.
/// Identifies the Y70 by the same hardware DeviceID prefix nexus-overlay uses
/// (<c>MONITOR\RTK0004</c>, the Realtek panel controller).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDisplayOrientationProvider : IDisplayOrientationProvider
{
    // Same set as nexus-overlay/src/PanelDisplay.cs - keep in sync.
    private static readonly string[] KnownDeviceIdPrefixes =
    {
        @"MONITOR\RTK0004", // HYTE Y70ti / Y70 Touch (Realtek panel controller)
    };

    public (bool Ok, string Error) SetY70Orientation(string orientation)
    {
        if (!TryParseOrientation(orientation, out var dmdo))
        {
            return (false, $"unknown orientation '{orientation}'");
        }
        var device = FindY70AdapterDevice();
        if (string.IsNullOrEmpty(device))
        {
            // Not an error: the Y70 simply is not attached. The persisted
            // orientation will be re-applied when it next connects.
            return (true, "");
        }

        var devMode = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettingsW(device, ENUM_CURRENT_SETTINGS, ref devMode))
        {
            return (false, "EnumDisplaySettings failed");
        }
        if (devMode.dmDisplayOrientation == dmdo) return (true, "");

        // Rotating between landscape <-> portrait flips the active resolution
        // axes. Failing to swap dmPelsWidth/dmPelsHeight makes Windows reject
        // the call with DISP_CHANGE_BADMODE.
        var fromPortrait = devMode.dmDisplayOrientation == DMDO_90 || devMode.dmDisplayOrientation == DMDO_270;
        var toPortrait = dmdo == DMDO_90 || dmdo == DMDO_270;
        if (fromPortrait != toPortrait)
        {
            (devMode.dmPelsWidth, devMode.dmPelsHeight) = (devMode.dmPelsHeight, devMode.dmPelsWidth);
        }
        devMode.dmDisplayOrientation = dmdo;
        devMode.dmFields |= DM_DISPLAYORIENTATION | DM_PELSWIDTH | DM_PELSHEIGHT;

        var rc = ChangeDisplaySettingsExW(device, ref devMode, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
        return rc switch
        {
            DISP_CHANGE_SUCCESSFUL => (true, ""),
            DISP_CHANGE_RESTART    => (true, "restart required"),
            DISP_CHANGE_BADMODE    => (false, "mode not supported"),
            DISP_CHANGE_BADFLAGS   => (false, "bad flags"),
            DISP_CHANGE_BADPARAM   => (false, "bad parameter"),
            DISP_CHANGE_FAILED     => (false, "ChangeDisplaySettings failed"),
            DISP_CHANGE_NOTUPDATED => (false, "registry not updated"),
            _                       => (false, $"unknown DISP_CHANGE {rc}"),
        };
    }

    private static bool TryParseOrientation(string value, out uint dmdo)
    {
        switch (value)
        {
            case "Landscape":         dmdo = DMDO_DEFAULT; return true;
            case "Portrait":          dmdo = DMDO_90;      return true;
            case "LandscapeFlipped":  dmdo = DMDO_180;     return true;
            case "PortraitFlipped":   dmdo = DMDO_270;     return true;
            default:                  dmdo = DMDO_DEFAULT; return false;
        }
    }

    /// <summary>
    /// Walks <c>EnumDisplayMonitors</c> + <c>EnumDisplayDevices</c> to find the
    /// adapter <c>\\.\DISPLAYn</c> hosting a HYTE panel. Mirrors
    /// <c>nexus-overlay/src/PanelDisplay.cs</c>. Calling
    /// <c>EnumDisplayDevicesW(null, ...)</c> to enumerate adapters is unreliable
    /// across CLR null-string marshaling paths; the EDM callback gives us a
    /// non-null <c>szDevice</c> for every active monitor.
    /// </summary>
    private static string FindY70AdapterDevice()
    {
        var monitors = EnumerateMonitorAdapters();
        foreach (var adapterName in monitors)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevicesW(adapterName, 0, ref dd, 0)) continue;
            var deviceId = dd.DeviceID ?? "";
            foreach (var prefix in KnownDeviceIdPrefixes)
            {
                if (deviceId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return adapterName;
                }
            }
        }
        return "";
    }

    private static List<string> EnumerateMonitorAdapters()
    {
        var list = new List<string>();
        bool Cb(IntPtr hMonitor, IntPtr _, IntPtr __, IntPtr ___)
        {
            var info = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfoW(hMonitor, ref info) && !string.IsNullOrEmpty(info.szDevice))
            {
                list.Add(info.szDevice);
            }
            return true;
        }
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Cb, IntPtr.Zero);
        return list;
    }

    // -- Win32 ---------------------------------------------------------------

    private const int CCHDEVICENAME = 32;
    private const int CCHDEVICESTRING = 128;
    private const int CCHDEVICEID = 128;
    private const int CCHDEVICEKEY = 128;

    private const uint ENUM_CURRENT_SETTINGS = unchecked((uint)-1);

    private const uint DMDO_DEFAULT = 0;
    private const uint DMDO_90 = 1;
    private const uint DMDO_180 = 2;
    private const uint DMDO_270 = 3;

    private const uint DM_PELSWIDTH = 0x00080000;
    private const uint DM_PELSHEIGHT = 0x00100000;
    private const uint DM_DISPLAYORIENTATION = 0x00800000;

    private const uint CDS_UPDATEREGISTRY = 0x00000001;

    private const int DISP_CHANGE_SUCCESSFUL = 0;
    private const int DISP_CHANGE_RESTART = 1;
    private const int DISP_CHANGE_FAILED = -1;
    private const int DISP_CHANGE_BADMODE = -2;
    private const int DISP_CHANGE_NOTUPDATED = -3;
    private const int DISP_CHANGE_BADFLAGS = -4;
    private const int DISP_CHANGE_BADPARAM = -5;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICESTRING)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICEID)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICEKEY)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string lpszDeviceName, uint iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(
        string lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);
}
#endif
