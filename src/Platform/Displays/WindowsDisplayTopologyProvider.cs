#if WINDOWS
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nexus.Service.Models.Displays;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Windows monitor topology via EnumDisplayMonitors + GetMonitorInfo, with
/// identity from <see cref="WindowsDisplayIdentity"/> (same id space as the
/// brightness provider) and per-monitor scale from shcore!GetDpiForMonitor.
/// Runs in the user-session helper: these APIs see no monitors from
/// Session 0, same constraint as the brightness provider.
///
/// The enumeration runs under a per-monitor-DPI-aware thread context so
/// rcMonitor comes back in physical pixels and positions across mixed-DPI
/// desktops stay mutually consistent.
/// </summary>
public sealed class WindowsDisplayTopologyProvider : IDisplayTopologyProvider
{
    public bool PositionsAvailable => true;

    public IReadOnlyList<RawDisplayInfo>? Enumerate()
    {
        var results = new List<RawDisplayInfo>();
        var previousContext = TrySetPerMonitorAwareV2();
        var touchMonitors = EnumerateTouchMonitors();
        var settingsNumbers = WindowsDisplayConfig.SourceNumbersByGdiName();
        try
        {
            bool Cb(IntPtr hMonitor, IntPtr _, IntPtr __, IntPtr ___)
            {
                try
                {
                    var info = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
                    if (!GetMonitorInfoW(hMonitor, ref info)) return true;

                    var (id, name, manufacturer, model, isInternal) = WindowsDisplayIdentity.ResolveIdentity(info.szDevice);
                    if (string.IsNullOrEmpty(name) || name.StartsWith(@"\\.\", StringComparison.Ordinal))
                    {
                        name = $"Display {results.Count + 1}";
                    }

                    var entry = new RawDisplayInfo
                    {
                        Id = id,
                        // Prefer the DISPLAYCONFIG source number Windows Settings
                        // shows; fall back to the GDI \\.\DISPLAYn ordinal when
                        // QueryDisplayConfig didn't resolve this adapter.
                        Number = settingsNumbers.TryGetValue(info.szDevice, out var num)
                            ? num
                            : WindowsDisplayIdentity.AdapterNumber(info.szDevice),
                        Name = name,
                        Manufacturer = manufacturer,
                        Model = model,
                        X = info.rcMonitor.Left,
                        Y = info.rcMonitor.Top,
                        Width = info.rcMonitor.Right - info.rcMonitor.Left,
                        Height = info.rcMonitor.Bottom - info.rcMonitor.Top,
                        IsPrimary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0,
                        IsInternal = isInternal,
                        IsTouch = touchMonitors.Contains(hMonitor),
                        RawHardwareId = WindowsDisplayIdentity.ReadMonitorDeviceId(info.szDevice),
                    };

                    // Native mode pixels: the registry-backed current mode is
                    // physical regardless of the caller's DPI awareness.
                    var mode = new DEVMODEW { dmSize = (ushort)Marshal.SizeOf<DEVMODEW>() };
                    if (EnumDisplaySettingsExW(info.szDevice, ENUM_CURRENT_SETTINGS, ref mode, 0))
                    {
                        entry.ResolutionWidth = (int)mode.dmPelsWidth;
                        entry.ResolutionHeight = (int)mode.dmPelsHeight;
                        entry.Orientation = DisplayOrientations.FromDmdo(mode.dmDisplayOrientation);
                    }
                    else
                    {
                        entry.ResolutionWidth = entry.Width;
                        entry.ResolutionHeight = entry.Height;
                    }

                    if (GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out var dpiX, out var _dpiY) == 0 && dpiX > 0)
                    {
                        entry.Scale = Math.Round(dpiX / 96.0, 2);
                    }

                    results.Add(entry);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[displays-win] topology entry failed: {ex.Message}");
                }
                return true;
            }
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Cb, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[displays-win] topology enumerate failed: {ex.Message}");
        }
        finally
        {
            RestoreThreadDpiContext(previousContext);
        }
        return results;
    }

    /// <summary>
    /// HMONITORs targeted by an integrated touch digitizer, via the Windows
    /// pointer-device association (GetPointerDevices). Touch pads and pens
    /// don't count - the gate decides whether touch-requiring panel widgets
    /// are placeable on a promoted monitor.
    /// </summary>
    private static HashSet<IntPtr> EnumerateTouchMonitors()
    {
        var touch = new HashSet<IntPtr>();
        try
        {
            uint count = 0;
            if (!GetPointerDevices(ref count, null) || count == 0) return touch;
            var devices = new POINTER_DEVICE_INFO[count];
            if (!GetPointerDevices(ref count, devices)) return touch;
            for (var i = 0; i < count && i < devices.Length; i++)
            {
                if (devices[i].pointerDeviceType == POINTER_DEVICE_TYPE_TOUCH && devices[i].monitor != IntPtr.Zero)
                    touch.Add(devices[i].monitor);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[displays-win] pointer-device enumeration failed: {ex.Message}");
        }
        return touch;
    }

    /// <summary>
    /// The helper process sets no process-wide DPI awareness, so without this
    /// the system would virtualize rcMonitor to 96-DPI units. Returns the
    /// previous context (IntPtr.Zero when the API is unavailable / failed).
    /// </summary>
    private static IntPtr TrySetPerMonitorAwareV2()
    {
        try { return SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
        catch { return IntPtr.Zero; }
    }

    private static void RestoreThreadDpiContext(IntPtr previous)
    {
        if (previous == IntPtr.Zero) return;
        try { SetThreadDpiAwarenessContext(previous); } catch { }
    }

    // -- P/Invoke -----------------------------------------------------------

    private const int CCHDEVICENAME = 32;
    private const uint MONITORINFOF_PRIMARY = 0x1;
    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int MDT_EFFECTIVE_DPI = 0;
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

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
    private struct DEVMODEW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
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
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
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
    private static extern bool EnumDisplaySettingsExW(string lpszDeviceName, int iModeNum, ref DEVMODEW lpDevMode, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    private const uint POINTER_DEVICE_TYPE_TOUCH = 0x00000003;
    private const int POINTER_DEVICE_PRODUCT_STRING_MAX = 520;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct POINTER_DEVICE_INFO
    {
        public uint displayOrientation;
        public IntPtr device;
        public uint pointerDeviceType;
        public IntPtr monitor;
        public uint startingCursorId;
        public ushort maxActiveContacts;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = POINTER_DEVICE_PRODUCT_STRING_MAX)]
        public string productString;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetPointerDevices(ref uint deviceCount, [Out] POINTER_DEVICE_INFO[]? pointerDevices);
}
#endif
